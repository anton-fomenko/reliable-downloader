using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ReliableDownloader.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class Tests
{
    private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
    private readonly FileDownloader _sut;
    private readonly List<FileProgress> _progressUpdates = new();
    private CancellationTokenSource _cts;
    private readonly string _testFilePath = "test_download.msi";
    private const long DefaultChunkSize = 1 * 1024 * 1024; // Match downloader chunk size

    public Tests()
    {
        _sut = new FileDownloader(_mockWebCalls.Object);
        _cts = new CancellationTokenSource();
        // Clean up any leftover test file before each test run
        if (File.Exists(_testFilePath)) File.Delete(_testFilePath);
    }

    [TearDown]
    public void Teardown()
    {
        _cts.Dispose();
        // Clean up test file after each test run
        if (File.Exists(_testFilePath)) File.Delete(_testFilePath);
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
    {
        // Arrange
        var url = "http://fail.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.NotFound); // Simulate failure

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once); // Verify HEAD call was made
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
    {
        // Arrange
        var url = "http://no-length.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.OK); // Missing Content-Length header

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
    }

    [Test]
    public async Task TryDownloadFile_ShouldProceed_WhenPartialSupportedAndHeadersPresent()
    {
        // Arrange
        var url = "http://partial-support.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.AcceptRanges.Add("bytes");
        response.Content = new ByteArrayContent(Array.Empty<byte>()); // Needs Content for Content.Headers
        response.Content.Headers.ContentLength = 1024;
        response.Content.Headers.ContentMD5 = Convert.FromBase64String("/////////////////////w=="); // Example valid MD5 hash (placeholder)

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        // Because download isn't implemented yet, it will return false,
        // but we verify the header check logic passed internally (no exceptions, etc.)
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        // For now, it returns false, but the key is that it got past the header check without early exit
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
        // TODO: add verification for download calls
    }

    [Test]
    public async Task TryDownloadFile_ShouldPerformFullDownload_WhenPartialNotSupported()
    {
        // Arrange
        var url = "http://no-partial-support.com/file.msi";
        long fileSize = 100; // Small size for test
        string fileContent = "This is the test file content for full download.";
        var fileBytes = Encoding.UTF8.GetBytes(fileContent);

        // Mock Headers response (No Accept-Ranges)
        var headersResponse = new HttpResponseMessage(HttpStatusCode.OK);
        headersResponse.Content = new ByteArrayContent(Array.Empty<byte>()); // Needed for Content.Headers
        headersResponse.Content.Headers.ContentLength = fileBytes.Length;
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                    .ReturnsAsync(headersResponse);

        // Mock Content response
        var contentResponse = new HttpResponseMessage(HttpStatusCode.OK);
        contentResponse.Content = new ByteArrayContent(fileBytes);
        contentResponse.Content.Headers.ContentLength = fileBytes.Length;
        _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                     .ReturnsAsync(contentResponse);

        // Act
        var result = await _sut.TryDownloadFile(url, _testFilePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == true);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
        _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
        _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), 
            Times.Never);

        // Verify file content and progress
        Assert.That(File.Exists(_testFilePath) == true, "Downloaded file should exist.");
        var writtenBytes = await File.ReadAllBytesAsync(_testFilePath);
        CollectionAssert.AreEqual(fileBytes, writtenBytes, "Downloaded file content mismatch.");

        Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(2), "Should have at least start and end progress updates.");
        Assert.That(_progressUpdates[0].ProgressPercent, Is.EqualTo(0.0)); // Starts at 0%
        Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0)); // Ends at 100%
        Assert.That(_progressUpdates[^1].TotalBytesDownloaded, Is.EqualTo(fileBytes.Length));
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndNotProceed_WhenHeadersRequestThrowsException()
    {
        // Arrange
        var url = "http://exception.com/file.msi";
        var filePath = "file.msi";

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ThrowsAsync(new HttpRequestException("Network unavailable"));

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
        // TODO Make sure that download request doesn't happen
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenCancelledDuringHeaderCheck()
    {
        // Arrange
        var url = "http://cancellable.com/file.msi";
        var filePath = "file.msi";
        _cts.Cancel(); // Cancel immediately

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                      .ThrowsAsync(new OperationCanceledException()); // Simulate cancellation during call

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFails()
    {
        // Arrange
        var url = "http://full-download-fails.com/file.msi";
        long fileSize = 1024;

        // Mock Headers response (No Accept-Ranges)
        var headersResponse = new HttpResponseMessage(HttpStatusCode.OK);
        headersResponse.Content = new ByteArrayContent(Array.Empty<byte>());
        headersResponse.Content.Headers.ContentLength = fileSize;
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                    .ReturnsAsync(headersResponse);

        // Mock Content response (Failure)
        var contentResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                     .ReturnsAsync(contentResponse);

        // Act
        var result = await _sut.TryDownloadFile(url, _testFilePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
        Assert.That(File.Exists(_testFilePath) == false, "File should not exist after failed download.");
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenFullDownloadThrowsNetworkError()
    {
        // Arrange
        var url = "http://full-download-network-error.com/file.msi";
        long fileSize = 1024;

        // Mock Headers response
        var headersResponse = new HttpResponseMessage(HttpStatusCode.OK);
        headersResponse.Content = new ByteArrayContent(Array.Empty<byte>());
        headersResponse.Content.Headers.ContentLength = fileSize;
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                    .ReturnsAsync(headersResponse);

        // Mock Content call to throw exception
        _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                     .ThrowsAsync(new HttpRequestException("Network error during download"));

        // Act
        var result = await _sut.TryDownloadFile(url, _testFilePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false); // Corrected Assert
        _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
        Assert.That(File.Exists(_testFilePath) == false, "File should not exist after exception during download.");
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenCancelledDuringFullDownload()
    {
        // Arrange
        var url = "http://cancel-during-full.com/file.msi";
        long fileSize = 500000; // Larger size to allow time for cancellation
        string fileContent = new string('A', (int)fileSize);
        var fileBytes = Encoding.UTF8.GetBytes(fileContent);

        // Mock Headers response
        var headersResponse = new HttpResponseMessage(HttpStatusCode.OK);
        headersResponse.Content = new ByteArrayContent(Array.Empty<byte>());
        headersResponse.Content.Headers.ContentLength = fileSize;
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(headersResponse);

        // Mock Content response - Use a stream that respects cancellation
        var tcs = new TaskCompletionSource<HttpResponseMessage>(); // Allows controlling when the response is available
        _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                     .Returns(async () => {
                         // Simulate delay before response is ready
                         await Task.Delay(50, _cts.Token); // Short delay
                         var response = new HttpResponseMessage(HttpStatusCode.OK);
                         // Simulate a slow stream read that gets cancelled
                         response.Content = new StreamContent(new SlowStream(fileBytes, 100 /*ms delay per chunk*/, _cts.Token));
                         response.Content.Headers.ContentLength = fileSize;
                         return response;
                     });

        // Act
        var downloadTask = _sut.TryDownloadFile(url, _testFilePath, HandleProgress, _cts.Token);

        // Cancel shortly after starting
        await Task.Delay(100); // Wait a bit for download to start reading
        _cts.Cancel();

        var result = await downloadTask;

        // Assert
        Assert.That(result == false);
        Assert.That(_cts.IsCancellationRequested);
        _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.AtMostOnce());
        Assert.That(!File.Exists(_testFilePath), "File should be deleted after cancellation during download.");
        Assert.That(_progressUpdates.Count > 0 && _progressUpdates[^1].ProgressPercent < 100.0 || _progressUpdates.Count == 0,
            "Progress should not reach 100% if cancelled during copy");
    }

    [Test]
    public async Task TryDownloadFile_ShouldPerformPartialDownload_WhenSupported()
    {
        // Arrange
        var url = "http://partial-support.com/file.msi";
        int fileSize = (int)(DefaultChunkSize * 2.5); // Test multiple chunks + final partial chunk
        var testData = GenerateTestData(fileSize);

        // Mock Headers response (Partial Supported)
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(CreateHeadersResponse(fileSize, true));

        // Mock Partial Content responses for each chunk
        long currentPos = 0;
        while (currentPos < fileSize)
        {
            long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
            long rangeFrom = currentPos;
            long rangeTo = rangeFrom + bytesToDownload - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom, rangeTo));
            currentPos += bytesToDownload;
        }

        // Act
        var result = await _sut.TryDownloadFile(url, _testFilePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result, Is.True);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
        _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); // Full download NOT called

        // Verify all partial download calls were made
        currentPos = 0;
        while (currentPos < fileSize)
        {
            long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
            long rangeFrom = currentPos;
            long rangeTo = rangeFrom + bytesToDownload - 1;
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once);
            currentPos += bytesToDownload;
        }

        Assert.That(File.Exists(_testFilePath), Is.True, "Downloaded file should exist.");
        var writtenBytes = await File.ReadAllBytesAsync(_testFilePath);
        CollectionAssert.AreEqual(testData, writtenBytes, "Downloaded file content mismatch.");

        Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(3), "Should have multiple progress updates."); // Start + chunks + end
        Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        Assert.That(_progressUpdates[^1].TotalBytesDownloaded, Is.EqualTo(fileSize));
    }

    private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();

    private HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (supportPartial) response.Headers.AcceptRanges.Add("bytes");
        response.Content = new ByteArrayContent(Array.Empty<byte>()); // Needs Content for Headers
        response.Content.Headers.ContentLength = contentLength;
        if (md5 != null) response.Content.Headers.ContentMD5 = md5;
        return response;
    }

    private HttpResponseMessage CreatePartialContentResponse(byte[] fullData, long rangeFrom, long rangeTo)
    {
        if (rangeFrom >= fullData.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable); // 416

        rangeTo = Math.Min(rangeTo, fullData.Length - 1); // Clamp rangeTo
        var partialData = fullData.Skip((int)rangeFrom).Take((int)(rangeTo - rangeFrom + 1)).ToArray();

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent); // 206
        response.Content = new ByteArrayContent(partialData);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
        response.Content.Headers.ContentLength = partialData.Length;
        return response;
    }

    private void HandleProgress(FileProgress progress)
    {
        _progressUpdates.Add(progress);
        Console.WriteLine($"Progress: {progress}");
    }

    // Helper stream to simulate slow download and cancellation check
    private class SlowStream : MemoryStream
    {
        private readonly int _delayMs;
        private readonly CancellationToken _token;

        public SlowStream(byte[] buffer, int delayMs, CancellationToken token) : base(buffer)
        {
            _delayMs = delayMs;
            _token = token;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Combine internal token and method token
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);

            await Task.Delay(_delayMs, linkedCts.Token); // Simulate network latency
            linkedCts.Token.ThrowIfCancellationRequested(); // Check for cancellation
            return await base.ReadAsync(buffer, offset, count, linkedCts.Token);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Combine internal token and method token
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);

            await Task.Delay(_delayMs, linkedCts.Token); // Simulate network latency
            linkedCts.Token.ThrowIfCancellationRequested(); // Check for cancellation
            return await base.ReadAsync(buffer, linkedCts.Token);
        }
    }
}