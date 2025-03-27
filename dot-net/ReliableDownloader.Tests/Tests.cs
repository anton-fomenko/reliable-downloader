using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ReliableDownloader.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class Tests
{
    private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
    private readonly FileDownloader _sut;
    private readonly List<FileProgress> _progressUpdates = new();
    private CancellationTokenSource _cts = null!;
    private readonly string _defaultTestFilePath = "test_download.msi";
    private List<string> _filesToCleanup = new();
    private const long DefaultChunkSize = 1 * 1024 * 1024;

    private const int TestMaxRetries = 1;
    private readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
    private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);

    public Tests()
    {
        _sut = new FileDownloader(
            _mockWebCalls.Object,
            maxRetries: TestMaxRetries,
            initialRetryDelay: TestInitialDelay,
            maxRetryDelay: TestMaxDelay
            );
    }

    [SetUp]
    public void Setup()
    {
        _cts = new CancellationTokenSource();
        _progressUpdates.Clear();
        _filesToCleanup = new List<string>();
        CleanUpFile(_defaultTestFilePath);
    }

    [TearDown]
    public void Teardown()
    {
        _cts?.Dispose();
        foreach (var filePath in _filesToCleanup) { CleanUpFile(filePath); }
        CleanUpFile(_defaultTestFilePath);
    }

    private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_file", string extension = ".msi")
    {
        var uniqueFilePath = $"{filePrefix}_{Guid.NewGuid()}{extension}";
        _filesToCleanup.Add(uniqueFilePath);
        CleanUpFile(uniqueFilePath);
        try { await testAction(uniqueFilePath); } finally { /* Teardown handles cleanup */ }
    }

    private void CleanUpFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); Console.WriteLine($"Cleaned up test file: {filePath}"); }
            catch (Exception ex) { Console.WriteLine($"!!! WARNING: Failed to clean up test file '{filePath}': {ex.Message}"); }
        }
    }

    // =========================================
    // Header & Basic Setup Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
    {
        // Arrange
        var url = "http://fail.com/file.msi";
        var filePath = _defaultTestFilePath; // No file interaction

        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response); 

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        // Keep verification as Times.Once(), as 404 is not retried
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once());
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
    {
        var url = "http://fail-exception.com/file.msi";
        var filePath = _defaultTestFilePath;
        // SetupSequence inherently handles multiple calls, no lambda needed here unless response needs regeneration
        _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // Fine as is for first call
                     .ThrowsAsync(new HttpRequestException("Network unavailable"));

        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
    }


    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
    {
        var url = "http://no-length.com/file.msi";
        var filePath = _defaultTestFilePath;
        // Use Lambda for header response generation
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .Returns(() => {
                         var response = new HttpResponseMessage(HttpStatusCode.OK);
                         response.Content = new ByteArrayContent(Array.Empty<byte>());
                         response.Content.Headers.ContentLength = null; // No length
                         return Task.FromResult(response);
                     }); // Changed to lambda

        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
    }

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://retry-header.com/file.msi";
            int fileSize = 100;
            var testData = GenerateTestData(fileSize);

            // SetupSequence is suitable here
            _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // Fail first
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false)); // Succeed second

            // Use Lambda for content response generation
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => {
                             var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                             contentResponse.Content.Headers.ContentLength = fileSize;
                             return Task.FromResult(contentResponse);
                         }); // Changed to lambda

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(2));
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldFail_WhenNetworkCallFailsMoreThanMaxRetries()
    {
        var url = "http://retry-fail-max.com/file.msi";
        var filePath = _defaultTestFilePath;
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ThrowsAsync(new HttpRequestException("Persistent network failure")); // ThrowsAsync is fine

        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        Assert.That(result, Is.False);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
        Assert.That(File.Exists(filePath), Is.False);
    }

    // =========================================
    // Full Download Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldPerformFullDownload_WhenPartialNotSupported()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://no-partial-support.com/file.msi";
            int fileSize = 100;
            var fileBytes = GenerateTestData(fileSize);

            // Use Lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

            // Use Lambda for content response
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => {
                             var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fileBytes) };
                             contentResponse.Content.Headers.ContentLength = fileSize;
                             return Task.FromResult(contentResponse);
                         });

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(fileBytes, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFails()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://full-download-fails.com/file.msi";
            long fileSize = 1024;

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

            // Use lambda for failing content response (new instance per retry)
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result == false);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            Assert.That(File.Exists(uniqueTestFilePath) == false);
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenFullDownloadThrowsNetworkError()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://full-download-network-error.com/file.msi";
            long fileSize = 1024;

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

            // ThrowsAsync is fine, doesn't involve disposable objects being returned prematurely
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .ThrowsAsync(new HttpRequestException("Network error during download"));

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result == false);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            Assert.That(File.Exists(uniqueTestFilePath) == false);
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldRestartFullDownload_WhenExistingFileIsLargerAndPartialNotSupported()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://local-larger-no-partial.com/file.msi";
            int serverFileSize = 1000;
            int localFileSize = 1500;
            var serverData = GenerateTestData(serverFileSize);
            var localData = GenerateTestData(localFileSize);
            await File.WriteAllBytesAsync(uniqueTestFilePath, localData);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(serverFileSize, false)));

            // Use lambda for content response
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => {
                             var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(serverData) };
                             contentResponse.Content.Headers.ContentLength = serverFileSize;
                             return Task.FromResult(contentResponse);
                         });

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(serverData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(serverFileSize));
        });
    }

    // =========================================
    // Partial Download Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldAttemptPartialDownload_WhenPartialSupportedAndHeadersPresent()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://partial-support.com/file.msi";
            int fileSize = 1024;
            var testData = GenerateTestData(fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _cts.Token))
                         .Returns(() => {
                             var partialData = testData.Skip(0).Take(fileSize).ToArray(); 
                             var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                             response.Content = new ByteArrayContent(partialData);
                             response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, fileSize - 1, fileSize);
                             response.Content.Headers.ContentLength = partialData.Length;
                             Console.WriteLine($"Mock for DownloadPartialContentAsync(0, {fileSize - 1}) invoked, returning new response."); // Add logging
                             return Task.FromResult(response);
                         });

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True); // Test fails here currently
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldPerformPartialDownload_WhenSupported()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://multi-partial-support.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 2.5);
            var testData = GenerateTestData(fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            long currentPos = 0;
            int expectedCalls = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                long rangeFrom = currentPos;
                long rangeTo = rangeFrom + bytesToDownload - 1;
                // Use lambda for each partial content response
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom, rangeTo)));
                currentPos += bytesToDownload;
                expectedCalls++;
            }

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls));

            currentPos = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token), Times.Once);
                currentPos += bytesToDownload;
            }

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldResumePartialDownload_WhenFileExists()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://resume-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);
            int existingSize = (int)(DefaultChunkSize / 2);
            var existingData = testData.Take(existingSize).ToArray();
            await File.WriteAllBytesAsync(uniqueTestFilePath, existingData);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            long currentPos = existingSize;
            int expectedCalls = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                long rangeFrom = currentPos;
                long rangeTo = rangeFrom + bytesToDownload - 1;
                // Use lambda for partial content responses
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom, rangeTo)));
                currentPos += bytesToDownload;
                expectedCalls++;
            }

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls));

            currentPos = existingSize;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token), Times.Once);
                currentPos += bytesToDownload;
            }

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenPartialDownloadChunkFails()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://partial-chunk-fails.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                        .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
            // Use lambda for first chunk
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

            long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
            // Use lambda for failing second chunk (new instance per retry)
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.False);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(DefaultChunkSize));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenPartialChunkFailsOnceThenSucceeds()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://retry-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
            // Use lambda for first chunk
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

            long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
            // SetupSequence needed for fail-then-succeed behavior
            _mockWebCalls.SetupSequence(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .ThrowsAsync(new HttpRequestException("Temporary chunk error")) // Fails once
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom2, rangeTo2)); // Succeeds on retry

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Called twice
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }


    [Test]
    public async Task TryDownloadFile_ShouldRestartDownload_WhenExistingFileIsLargerThanTotalSize()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://local-larger.com/file.msi";
            int serverFileSize = 1000;
            int localFileSize = 1500;
            var testData = GenerateTestData(serverFileSize);
            var localData = GenerateTestData(localFileSize);
            await File.WriteAllBytesAsync(uniqueTestFilePath, localData);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(serverFileSize, true)));

            long rangeFrom = 0, rangeTo = serverFileSize - 1;
            // Use lambda for partial content response
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                         .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom, rangeTo)));

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once);
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(serverFileSize));
        });
    }

    // =========================================
    // Cancellation Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenCancelledDuringHeaderCheck()
    {
        var url = "http://cancellable-header.com/file.msi";
        var filePath = _defaultTestFilePath;

        // Mock with delay and cancellation check
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                      .Returns(async () => {
                          await Task.Delay(100, _cts.Token);
                          _cts.Token.ThrowIfCancellationRequested(); // Throw if cancelled during delay
                          // This line likely won't be reached if cancelled quickly
                          return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                      });

        var downloadTask = _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);
        await Task.Delay(50); // Wait less than the delay
        _cts.Cancel();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await downloadTask);
        Assert.That(_cts.IsCancellationRequested);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.AtMostOnce());
    }


    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenCancelledDuringFullDownload()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://cancel-during-full.com/file.msi";
            long fileSize = 500000;
            var fileBytes = GenerateTestData((int)fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

            // Use lambda for content response, creating SlowStream inside
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(async () => {
                             await Task.Delay(20, _cts.Token);
                             var response = new HttpResponseMessage(HttpStatusCode.OK);
                             response.Content = new StreamContent(new SlowStream(fileBytes, 50, _cts.Token)); // Pass test's token
                             response.Content.Headers.ContentLength = fileSize;
                             return response;
                         });

            var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);
            await Task.Delay(100); // Wait for download writing to start
            _cts.Cancel();
            var result = await downloadTask;

            Assert.That(result == false);
            Assert.That(_cts.IsCancellationRequested);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once());
            Assert.That(!File.Exists(uniqueTestFilePath), "File should be deleted after cancellation during full download.");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndKeepPartial_WhenCancelledDuringPartialDownload()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://cancel-during-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 2.5);
            var testData = GenerateTestData(fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                        .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
            // Use lambda for first chunk
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

            long rangeFrom2 = DefaultChunkSize, rangeTo2 = rangeFrom2 + DefaultChunkSize - 1;
            // Use lambda for second chunk with SlowStream
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                        .Returns(async () => {
                            await Task.Delay(20, _cts.Token);
                            var chunkData = testData.Skip((int)rangeFrom2).Take((int)(rangeTo2 - rangeFrom2 + 1)).ToArray();
                            var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                            response.Content = new StreamContent(new SlowStream(chunkData, 50, _cts.Token)); // Pass test's token
                            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom2, rangeTo2, fileSize);
                            response.Content.Headers.ContentLength = chunkData.Length;
                            return response;
                        });

            var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);
            await Task.Delay(100); // Wait for download writing to start
            _cts.Cancel();
            var result = await downloadTask;

            Assert.That(result, Is.False);
            Assert.That(_cts.IsCancellationRequested, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.AtMostOnce());
            Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should be kept.");
            Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.LessThan(fileSize));
        });
    }

    // =========================================
    // MD5 Integrity Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenMd5Matches()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://md5-match.com/file.msi";
            int fileSize = 500;
            var testData = GenerateTestData(fileSize);
            byte[] correctMd5; using (var md5 = MD5.Create()) { correctMd5 = md5.ComputeHash(testData); }

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false, correctMd5)));

            // Use lambda for content response
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => {
                             var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                             contentResponse.Content.Headers.ContentLength = fileSize;
                             return Task.FromResult(contentResponse);
                         });

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldFailAndCleanup_WhenMd5Mismatch()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://md5-mismatch.com/file.msi";
            int fileSize = 500;
            var testData = GenerateTestData(fileSize);
            byte[] correctMd5; using (var md5 = MD5.Create()) { correctMd5 = md5.ComputeHash(testData); }
            byte[] incorrectMd5 = correctMd5.Select(b => (byte)(~b)).ToArray();

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false, incorrectMd5)));

            // Use lambda for content response
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => {
                             var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                             contentResponse.Content.Headers.ContentLength = fileSize;
                             return Task.FromResult(contentResponse);
                         });


            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.False);
            Assert.That(File.Exists(uniqueTestFilePath), Is.False);
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenMd5HeaderMissing()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://md5-missing.com/file.msi";
            int fileSize = 500;
            var testData = GenerateTestData(fileSize);

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false, md5: null)));

            // Use lambda for content response
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(() => {
                             var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                             contentResponse.Content.Headers.ContentLength = fileSize;
                             return Task.FromResult(contentResponse);
                         });

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    // =========================================
    // Progress Reporting Tests
    // =========================================
    [Test]
    public async Task TryDownloadFile_ShouldReportEstimatedTimeRemaining()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            var url = "http://progress-reporting.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);
            int simulatedNetworkDelayMs = 100;

            // Use lambda for header response
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

            long currentPos = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                long rangeFrom = currentPos;
                long rangeTo = rangeFrom + bytesToDownload - 1;
                long currentRangeFrom = rangeFrom; // Capture loop variables
                long currentRangeTo = rangeTo;

                // Use lambda for delayed partial response
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, currentRangeFrom, currentRangeTo, _cts.Token))
                             .Returns(async () => {
                                 await Task.Delay(simulatedNetworkDelayMs, _cts.Token);
                                 return CreatePartialContentResponse(testData, currentRangeFrom, currentRangeTo);
                             });
                currentPos += bytesToDownload;
            }

            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            Assert.That(result, Is.True);
            Assert.That(_progressUpdates.Count, Is.GreaterThan(1));
            var firstMeaningfulUpdate = _progressUpdates.FirstOrDefault(p => p.TotalBytesDownloaded > 0 && p.EstimatedRemaining.HasValue && p.EstimatedRemaining.Value > TimeSpan.Zero);
            Assert.That(firstMeaningfulUpdate, Is.Not.Null);
            var updateAfterMidpoint = _progressUpdates.FirstOrDefault(p => p.TotalBytesDownloaded >= fileSize / 2 && p.EstimatedRemaining.HasValue);
            var lastMeaningfulUpdate = _progressUpdates.LastOrDefault(p => p.ProgressPercent < 100.0 && p.EstimatedRemaining.HasValue && p.EstimatedRemaining.Value > TimeSpan.Zero);
            Assert.That(lastMeaningfulUpdate, Is.Not.Null.Or.EqualTo(firstMeaningfulUpdate));
            Assert.That(updateAfterMidpoint, Is.Not.Null);
            if (firstMeaningfulUpdate != lastMeaningfulUpdate && updateAfterMidpoint != null && lastMeaningfulUpdate != null && updateAfterMidpoint.EstimatedRemaining.HasValue && lastMeaningfulUpdate.EstimatedRemaining.HasValue)
            {
                Console.WriteLine($"Midpoint Estimate: {updateAfterMidpoint.EstimatedRemaining}, Last Meaningful Estimate: {lastMeaningfulUpdate.EstimatedRemaining}");
                Assert.That(lastMeaningfulUpdate.EstimatedRemaining.Value.TotalMilliseconds, Is.LessThan(updateAfterMidpoint.EstimatedRemaining.Value.TotalMilliseconds + (simulatedNetworkDelayMs * 2)));
            }
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
            Assert.That(_progressUpdates[^1].EstimatedRemaining, Is.EqualTo(TimeSpan.Zero).Or.Null);

        }, filePrefix: "progress_test");
    }

    // =========================================
    // Private Helper Methods
    // =========================================
    // ... (GenerateTestData, CreateHeadersResponse, CreatePartialContentResponse, HandleProgress, SlowStream are unchanged) ...
    private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();

    private HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (supportPartial) response.Headers.AcceptRanges.Add("bytes");
        response.Content = new ByteArrayContent(Array.Empty<byte>()); // Need content object to set headers
        response.Content.Headers.ContentLength = contentLength;
        if (md5 != null) response.Content.Headers.ContentMD5 = md5;
        return response;
    }

    private HttpResponseMessage CreatePartialContentResponse(byte[] fullData, long rangeFrom, long rangeTo)
    {
        if (rangeFrom >= fullData.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);

        rangeTo = Math.Min(rangeTo, fullData.Length - 1);
        var partialData = fullData.Skip((int)rangeFrom).Take((int)(rangeTo - rangeFrom + 1)).ToArray();

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
        // Use a new ByteArrayContent each time
        response.Content = new ByteArrayContent(partialData);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
        response.Content.Headers.ContentLength = partialData.Length;
        return response;
    }

    private void HandleProgress(FileProgress progress)
    {
        _progressUpdates.Add(progress);
        // Console.WriteLine($"Progress: {progress}"); // Keep commented out unless debugging progress
    }

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
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
            try
            {
                if (_delayMs > 0) await Task.Delay(_delayMs, linkedCts.Token);
                linkedCts.Token.ThrowIfCancellationRequested();
                return await base.ReadAsync(buffer, offset, count, linkedCts.Token);
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
            try
            {
                if (_delayMs > 0) await Task.Delay(_delayMs, linkedCts.Token);
                linkedCts.Token.ThrowIfCancellationRequested();
                return await base.ReadAsync(buffer, linkedCts.Token);
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }
    }
}