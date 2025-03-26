using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Net;
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

    private void HandleProgress(FileProgress progress)
    {
        _progressUpdates.Add(progress);
        Console.WriteLine($"Progress: {progress}");
    }
}