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
        // NOTE: CancellationTokenSource and file cleanup are handled in Setup/Teardown
    }

    // Use Setup and Teardown for per-test initialization and cleanup
    [SetUp]
    public void Setup()
    {
        _cts = new CancellationTokenSource();
        _progressUpdates.Clear();
        _filesToCleanup = new List<string>(); // Reset list for each test

        // Clean up the default path before each test for tests that might use it
        CleanUpFile(_defaultTestFilePath);
    }

    [TearDown]
    public void Teardown()
    {
        _cts?.Dispose();

        // Clean up any files generated during the test
        foreach (var filePath in _filesToCleanup)
        {
            CleanUpFile(filePath);
        }
        // Also clean up the default path again
        CleanUpFile(_defaultTestFilePath);
    }

    // --- Reusable Helper for Unique File Path ---
    private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_file", string extension = ".msi")
    {
        var uniqueFilePath = $"{filePrefix}_{Guid.NewGuid()}{extension}";
        _filesToCleanup.Add(uniqueFilePath); // Add to list for teardown cleanup

        // Ensure clean state before test action
        CleanUpFile(uniqueFilePath);

        try
        {
            // Execute the core test logic, passing the unique path
            await testAction(uniqueFilePath);
        }
        finally
        {
            // Teardown will handle the cleanup now
            // CleanUpFile(uniqueFilePath); // Removed direct cleanup here
        }
    }

    // Helper to encapsulate file deletion and ignore errors
    private void CleanUpFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to clean up test file '{filePath}': {ex.Message}");
            }
        }
    }
    // --- End Helpers ---

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
    {
        // Arrange
        var url = "http://fail.com/file.msi";
        // This test doesn't interact with the file system, so default path is fine.
        var filePath = _defaultTestFilePath;
        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
    {
        // Arrange
        var url = "http://fail-exception.com/file.msi";
        var filePath = _defaultTestFilePath;
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response)
                     .ThrowsAsync(new HttpRequestException("Network unavailable"));

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
    }


    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
    {
        // Arrange
        var url = "http://no-length.com/file.msi";
        var filePath = _defaultTestFilePath;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        response.Content.Headers.ContentLength = null;

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
    }

    [Test]
    public async Task TryDownloadFile_ShouldAttemptPartialDownload_WhenPartialSupportedAndHeadersPresent()
    {
        // Use the helper for a unique file path
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

            Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Downloaded file should exist.");
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes, "Downloaded file content mismatch.");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldPerformFullDownload_WhenPartialNotSupported()
    {
        // Use the helper for a unique file path to ensure clean state
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://no-partial-support.com/file.msi";
            int fileSize = 100;
            var fileBytes = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false));

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK);
            contentResponse.Content = new ByteArrayContent(fileBytes);
            contentResponse.Content.Headers.ContentLength = fileBytes.Length;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == true);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);

            Assert.That(File.Exists(uniqueTestFilePath) == true, "Downloaded file should exist.");
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(fileBytes, writtenBytes, "Downloaded file content mismatch.");

            Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(_progressUpdates[0].ProgressPercent, Is.EqualTo(0.0));
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
            Assert.That(_progressUpdates[^1].TotalBytesDownloaded, Is.EqualTo(fileBytes.Length));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenCancelledDuringHeaderCheck()
    {
        // Arrange
        var url = "http://cancellable.com/file.msi";
        var filePath = _defaultTestFilePath; // No file interaction needed

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                      .Returns(async () => {
                          await Task.Delay(100, _cts.Token);
                          return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                      });

        // Act
        var downloadTask = _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);
        await Task.Delay(50);
        _cts.Cancel();

        var result = await downloadTask;

        // Assert
        Assert.That(result == false);
        Assert.That(_cts.IsCancellationRequested);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.AtMostOnce());
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFails()
    {
        // Use the helper for a unique file path to ensure clean state and proper cleanup
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
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            Assert.That(File.Exists(uniqueTestFilePath) == false, "File should not exist after failed download.");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenFullDownloadThrowsNetworkError()
    {
        // Use the helper for a unique file path to ensure clean state and proper cleanup
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

            // Verify download was attempted initial call + configured retries
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Expect 2 calls
            Assert.That(File.Exists(uniqueTestFilePath) == false, "File should not exist after exception during download.");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenCancelledDuringFullDownload()
    {
        // Use the helper for a unique file path to ensure clean state and proper cleanup
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://cancel-during-full.com/file.msi";
            long fileSize = 500000;
            var fileBytes = GenerateTestData((int)fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false));

            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                         .Returns(async () => {
                             await Task.Delay(20, _cts.Token);
                             var response = new HttpResponseMessage(HttpStatusCode.OK);
                             response.Content = new StreamContent(new SlowStream(fileBytes, 50, _cts.Token));
                             response.Content.Headers.ContentLength = fileSize;
                             return response;
                         });

            // Act
            var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            await Task.Delay(100);
            _cts.Cancel();

            var result = await downloadTask;

            // Assert
            Assert.That(result == false);
            Assert.That(_cts.IsCancellationRequested);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once());
            Assert.That(!File.Exists(uniqueTestFilePath), "File should be deleted after cancellation during download.");
            Assert.That(_progressUpdates.Count > 0 && _progressUpdates[^1].ProgressPercent < 100.0 || _progressUpdates.Count == 0,
                "Progress should not reach 100% if cancelled during copy");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldPerformPartialDownload_WhenSupported()
    {
        // Use the helper for a unique file path
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://partial-support.com/file.msi";
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

            // Check each specific range call was made once
            currentPos = 0;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                long rangeFrom = currentPos;
                long rangeTo = rangeFrom + bytesToDownload - 1;
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once);
                currentPos += bytesToDownload;
            }

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes);

            Assert.That(_progressUpdates.Count, Is.GreaterThanOrEqualTo(expectedCalls + 1));
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
            Assert.That(_progressUpdates[^1].TotalBytesDownloaded, Is.EqualTo(fileSize));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldResumePartialDownload_WhenFileExists()
    {
        // This test explicitly creates a file first, so use the helper
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://resume-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);
            int existingSize = (int)(DefaultChunkSize / 2);
            var existingData = testData.Take(existingSize).ToArray();

            // Create the partial file using the unique path
            await File.WriteAllBytesAsync(uniqueTestFilePath, existingData);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long currentPos = existingSize;
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
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls));

            currentPos = existingSize;
            while (currentPos < fileSize)
            {
                long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                long rangeFrom = currentPos;
                long rangeTo = rangeFrom + bytesToDownload - 1;
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once);
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
        // Use the helper as this involves writing partial files
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://partial-chunk-fails.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 1.5);
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long rangeFrom1 = 0;
            long rangeTo1 = DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1));

            long rangeFrom2 = DefaultChunkSize;
            long rangeTo2 = fileSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.False);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1));

            Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should still exist on failure.");
            var currentSize = new FileInfo(uniqueTestFilePath).Length;
            Assert.That(currentSize, Is.EqualTo(DefaultChunkSize), "File size should reflect successfully downloaded chunks.");
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalseAndKeepPartial_WhenCancelledDuringPartialDownload()
    {
        // Use the helper as this involves writing partial files
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://cancel-during-partial.com/file.msi";
            int fileSize = (int)(DefaultChunkSize * 2.5);
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                        .ReturnsAsync(CreateHeadersResponse(fileSize, true));

            long rangeFrom1 = 0;
            long rangeTo1 = DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1));

            long rangeFrom2 = DefaultChunkSize;
            long rangeTo2 = rangeFrom2 + DefaultChunkSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                         .Returns(async () => {
                             await Task.Delay(150, _cts.Token);
                             _cts.Token.ThrowIfCancellationRequested();
                             return CreatePartialContentResponse(testData, rangeFrom2, rangeTo2);
                         });

            // Act
            var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            await Task.Delay(100);
            _cts.Cancel();

            var result = await downloadTask;

            // Assert
            Assert.That(result, Is.False);
            Assert.That(_cts.IsCancellationRequested, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.AtMostOnce());

            Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should be kept.");
            var currentSize = new FileInfo(uniqueTestFilePath).Length;
            Assert.That(currentSize, Is.EqualTo(DefaultChunkSize), "File size should reflect completed chunks.");
            Assert.That(_progressUpdates.Count > 0 && _progressUpdates[^1].ProgressPercent < 100.0, Is.True);
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldRestartDownload_WhenExistingFileIsLargerThanTotalSize()
    {
        // This test explicitly creates a file first, so use the helper
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://local-larger.com/file.msi";
            int serverFileSize = 1000;
            int localFileSize = 1500;
            var testData = GenerateTestData(serverFileSize);
            var localData = GenerateTestData(localFileSize);

            await File.WriteAllBytesAsync(uniqueTestFilePath, localData); // Create oversized file

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(serverFileSize, true));

            long rangeFrom = 0;
            long rangeTo = serverFileSize - 1;
            _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                         .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom, rangeTo));

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once);

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(testData, writtenBytes);
            Assert.That(writtenBytes.Length, Is.EqualTo(serverFileSize));

            Assert.That(_progressUpdates.Any(p => p.TotalBytesDownloaded == 0 && p.ProgressPercent == 0.0), Is.True);
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }

    [Test]
    public async Task TryDownloadFile_ShouldRestartFullDownload_WhenExistingFileIsLargerAndPartialNotSupported()
    {
        // This test explicitly creates a file first, so use the helper
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://local-larger-no-partial.com/file.msi";
            int serverFileSize = 1000;
            int localFileSize = 1500;
            var serverData = GenerateTestData(serverFileSize);
            var localData = GenerateTestData(localFileSize);

            await File.WriteAllBytesAsync(uniqueTestFilePath, localData);

            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(CreateHeadersResponse(serverFileSize, false));

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(serverData) };
            contentResponse.Content.Headers.ContentLength = serverFileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);

            Assert.That(File.Exists(uniqueTestFilePath), Is.True);
            var writtenBytes = await File.ReadAllBytesAsync(uniqueTestFilePath);
            CollectionAssert.AreEqual(serverData, writtenBytes);
            Assert.That(writtenBytes.Length, Is.EqualTo(serverFileSize));

            Assert.That(_progressUpdates.Any(p => p.TotalBytesDownloaded == 0 && p.ProgressPercent == 0.0), Is.True);
            Assert.That(_progressUpdates[^1].ProgressPercent, Is.EqualTo(100.0));
        });
    }


    [Test]
    public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
    {
        // Use the helper for a unique file path
        await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
        {
            // Arrange
            var url = "http://retry-header.com/file.msi";
            int fileSize = 100;
            var testData = GenerateTestData(fileSize);

            _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                         .ReturnsAsync(CreateHeadersResponse(fileSize, false));

            var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
            contentResponse.Content.Headers.ContentLength = fileSize;
            _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token)).ReturnsAsync(contentResponse);

            // Act
            var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.True);
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(2));
            _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once);
            CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
        });
    }

    // --- Helper Methods ---

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