using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography; // Added for MD5 tests
using System.Text;

namespace ReliableDownloader.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class Tests
{
    private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
    private readonly FileDownloader _sut;
    private readonly List<FileProgress> _progressUpdates = new();
    private CancellationTokenSource _cts = null!; // Initialize in Setup
    private readonly string _defaultTestFilePath = "test_download.msi"; // Default path for simple tests
    private List<string> _filesToCleanup = new(); // Keep track of generated files
    private const long DefaultChunkSize = 1 * 1024 * 1024; // Match downloader chunk size

    // --- Test-specific retry settings ---
    private const int TestMaxRetries = 1; // Reduce retries for faster tests
    private readonly TimeSpan TestInitialDelay = TimeSpan.Zero; // No delay
    private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1); // Minimal delay

    public Tests()
    {
        // Instantiate with fast test settings
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
        foreach (var filePath in _filesToCleanup)
        {
            CleanUpFile(filePath);
        }
        CleanUpFile(_defaultTestFilePath);
    }

    // --- Reusable Helper for Unique File Path ---
    private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_file", string extension = ".msi")
    {
        var uniqueFilePath = $"{filePrefix}_{Guid.NewGuid()}{extension}";
        _filesToCleanup.Add(uniqueFilePath);
        CleanUpFile(uniqueFilePath); // Ensure clean before start

        try
        {
            await testAction(uniqueFilePath);
        }
        finally
        {
            // Teardown handles cleanup via _filesToCleanup list
        }
    }

    private void CleanUpFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); }
            catch (Exception ex) { Console.WriteLine($"Warning: Failed to clean up test file '{filePath}': {ex.Message}"); }
        }
    }
    // --- End Helpers ---

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
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Retried
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
    {
        // Arrange
        var url = "http://fail-exception.com/file.msi";
        var filePath = _defaultTestFilePath; // No file interaction
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response) // Fails first
                     .ThrowsAsync(new HttpRequestException("Network unavailable")); // Throws on retry

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Retried
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
    {
        // Arrange
        var url = "http://no-length.com/file.msi";
        var filePath = _defaultTestFilePath; // No file interaction
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        response.Content.Headers.ContentLength = null; // Explicitly null

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once); // No retry needed
    }

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
    {
        // Uses unique file helper for consistency, though default might work
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://retry-header.com/file.msi";
            int fileSize = 100;
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // Fail first
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false)); // Succeed second

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
            contentResponse.Content.Headers.ContentLength = fileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(2)); // Called twice
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once); // Download once
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldFail_WhenNetworkCallFailsMoreThanMaxRetries()
    {
        // Arrange
        var url = "http://retry-fail-max.com/file.msi";
        var filePath = _defaultTestFilePath; // No file interaction needed

        // Fail headers call repeatedly with exception
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ThrowsAsync(new HttpRequestException("Persistent network failure"));

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result, Is.False); // Should fail after max retries
        // Verify based on configured TestMaxRetries
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
        Assert.That(File.Exists(filePath), Is.False); // File should not have been created
    }

    // =========================================
    // Full Download Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldPerformFullDownload_WhenPartialNotSupported()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://no-partial-support.com/file.msi";
            int fileSize = 100;
            var fileBytes = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false)); // No partial

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fileBytes) };
            contentResponse.Content.Headers.ContentLength = fileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(fileBytes, writtenBytes);
            Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFails()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://full-download-fails.com/file.msi";
            long fileSize = 1024;

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false));

            var contentResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == false);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Retried
            Assert.That(File.Exists(uniqueTestFilePath) == false); // Should not exist (or be deleted by SUT - current SUT doesn't explicitly delete on HTTP failure)
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenFullDownloadThrowsNetworkError()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://full-download-network-error.com/file.msi";
            long fileSize = 1024;

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false));

            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .ThrowsAsync(new HttpRequestException("Network error during download"));

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == false);
            // Verify download attempted initial call + configured retries (because HttpRequestException is caught by retry)
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            Assert.That(File.Exists(uniqueTestFilePath) == false, "File should not exist after exception during download.");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldRestartFullDownload_WhenExistingFileIsLargerAndPartialNotSupported()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://local-larger-no-partial.com/file.msi";
            int serverFileSize = 1000;
            int localFileSize = 1500;
            var serverData = GenerateTestData(serverFileSize);
            var localData = GenerateTestData(localFileSize);

            await File.WriteAllBytesAsync(uniqueTestFilePath, localData); // Create oversized file

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(serverFileSize, false)); // No partial

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(serverData) };
            contentResponse.Content.Headers.ContentLength = serverFileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once); // Full download once

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(serverData, writtenBytes);
            Assert.That(writtenBytes.Length, Is.EqualTo(serverFileSize));
            Assert.That(_progressUpdates.Any(p => p.TotalBytesDownloaded == 0 && p.ProgressPercent == 0.0), Is.True);
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
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
            // Arrange
            var url = "http://partial-support.com/file.msi";
            int fileSize = 1024;
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, 0, fileSize - 1));

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes);
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldPerformPartialDownload_WhenSupported()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://multi-partial-support.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 2.5); // > 2 chunks
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long currentPos = 0;
            int expectedCalls = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                long rangeFrom = currentPos;
                long rangeTo = rangeFrom + bytesToDownload - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                             .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom, rangeTo));
                currentPos += bytesToDownload;
                expectedCalls++;
            }

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls));

            currentPos = 0; // Verify specific calls
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token), Times.Once);
                currentPos += bytesToDownload;
            }

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes);
            Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(expectedCalls + 1));
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldResumePartialDownload_WhenFileExists()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://resume-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);
            int existingSize = (int)(DefaultChunkSize / 2);
            var existingData = testData.Take(existingSize).ToArray();

            await File.WriteAllBytesAsync(uniqueTestFilePath, existingData); // Create partial file

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long currentPos = existingSize;
            int expectedCalls = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token))
                             .ReturnsAsync(CreatePartialContentResponse(testData, currentPos, currentPos + bytesToDownload - 1));
                currentPos += bytesToDownload;
                expectedCalls++;
            }

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls));

            currentPos = existingSize; // Verify specific resumed calls
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token), Times.Once);
                currentPos += bytesToDownload;
            }

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes);
            Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(expectedCalls + 1));
            Assert.That(_progressUpdates[0].TotalBytesDownloaded, Is.EqualTo(existingSize));
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenPartialDownloadChunkFails()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://partial-chunk-fails.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1));

            long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError)); // Persistent failure

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.False);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Retried

            Assert.That(File.Exists(uniqueTestFilePath), Is.True); // Partial file kept
            var currentSize = new FileInfo(uniqueTestFilePath).Length;
            Assert.That(currentSize, Is.EqualTo(DefaultChunkSize));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenPartialChunkFailsOnceThenSucceeds()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://retry-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1));

            long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
            _mockWebCalls.SetupSequence(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .ThrowsAsync(new HttpRequestException("Temporary chunk error")) // Fails once
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom2, rangeTo2)); // Succeeds on retry

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True); // Succeeds overall
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk once
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(2)); // Second chunk twice (initial fail + retry success)
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }


    [Test]
    public async Task TryDownloadFile_ShouldRestartDownload_WhenExistingFileIsLargerThanTotalSize()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://local-larger.com/file.msi";
            int serverFileSize = 1000;
            int localFileSize = 1500;
            var testData = GenerateTestData(serverFileSize);
            var localData = GenerateTestData(localFileSize);

            await File.WriteAllBytesAsync(uniqueTestFilePath, localData); // Oversized file

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(serverFileSize, true)); // Partial support

            long rangeFrom = 0, rangeTo = serverFileSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom, rangeTo));

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once); // Download from 0 once

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes);
            Assert.That(writtenBytes.Length, Is.EqualTo(serverFileSize));
            Assert.That(_progressUpdates.Any(p => p.TotalBytesDownloaded == 0 && p.ProgressPercent == 0.0), Is.True);
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }

    // =========================================
    // Cancellation Tests
    // =========================================

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenCancelledDuringFullDownload()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://cancel-during-full.com/file.msi";
            long fileSize = 500000;
            var fileBytes = GenerateTestData((int)fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false)); // No partial

            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(async () => {
                             await Task.Delay(20, _cts.Token); // Short delay, then slow stream
                             var response = new HttpResponseMessage(HttpStatusCode.OK);
                             response.Content = new StreamContent(new SlowStream(fileBytes, 50, _cts.Token));
                             response.Content.Headers.ContentLength = fileSize;
                             return response;
                         });

            // Act
            var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);
            await Task.Delay(100); // Wait for download to start writing
            _cts.Cancel();
            var result = await downloadTask;

            // Assert
            Assert.That(result == false);
            Assert.That(_cts.IsCancellationRequested);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once());
            Assert.That(!File.Exists(uniqueTestFilePath), "File should be deleted after cancellation during full download.");
            Assert.That(_progressUpdates.Count > 0 && _progressUpdates[^1].ProgressPercent < 100.0 || _progressUpdates.Count == 0);
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndKeepPartial_WhenCancelledDuringPartialDownload()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://cancel-during-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 2.5);
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                        .ReturnsAsync(CreateHeadersResponse(fileSize, true)); // Partial support

            long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)); // First chunk succeeds

            long rangeFrom2 = DefaultChunkSize, rangeTo2 = rangeFrom2 + DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .Returns(async () => { // Second chunk is slow
                             await Task.Delay(150, _cts.Token);
                             _cts.Token.ThrowIfCancellationRequested();
                             return CreatePartialContentResponse(testData, rangeFrom2, rangeTo2);
                         });

            // Act
            var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);
            await Task.Delay(100); // Wait for first chunk + start of second chunk's delay
            _cts.Cancel();
            var result = await downloadTask;

            // Assert
            Assert.That(result, Is.False);
            Assert.That(_cts.IsCancellationRequested, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk called
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.AtMostOnce()); // Second attempted at most once

            Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should be kept.");
            var currentSize = new FileInfo(uniqueTestFilePath).Length;
            Assert.That(currentSize, Is.EqualTo(DefaultChunkSize), "File size should reflect completed chunks.");
            Assert.That(_progressUpdates.Count > 0 && _progressUpdates[^1].ProgressPercent < 100.0, Is.True);
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
            // Arrange
            var url = "http://md5-match.com/file.msi";
            int fileSize = 500;
            var testData = GenerateTestData(fileSize);
            byte[] correctMd5;
            using (var md5 = MD5.Create()) { correctMd5 = md5.ComputeHash(testData); }

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false, correctMd5)); // Correct MD5

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
            contentResponse.Content.Headers.ContentLength = fileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True); // Success
            Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File exists
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldFailAndCleanup_WhenMd5Mismatch()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://md5-mismatch.com/file.msi";
            int fileSize = 500;
            var testData = GenerateTestData(fileSize);
            byte[] correctMd5;
            using (var md5 = MD5.Create()) { correctMd5 = md5.ComputeHash(testData); }
            byte[] incorrectMd5 = correctMd5.Select(b => (byte)(~b)).ToArray(); // Different hash

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false, incorrectMd5)); // INCORRECT MD5

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
            contentResponse.Content.Headers.ContentLength = fileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.False); // Should fail integrity
            Assert.That(File.Exists(uniqueTestFilePath), Is.False); // File should be deleted
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenMd5HeaderMissing()
    {
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://md5-missing.com/file.msi";
            int fileSize = 500;
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false, md5: null)); // MD5 is null

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
            contentResponse.Content.Headers.ContentLength = fileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True); // Succeeds (verification skipped)
            Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File exists
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    // =========================================
    // Private Helper Methods
    // =========================================

    private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();

    private HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (supportPartial) response.Headers.AcceptRanges.Add("bytes");
        response.Content = new ByteArrayContent(Array.Empty<byte>());
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