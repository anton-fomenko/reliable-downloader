using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy; // Needed for CollectionAssert if using older NUnit structure
using System.Net;
using System.Net.Http.Headers;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_RetryTests
    {
        // Copied from original Tests.cs for context
        private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
        private readonly FileDownloader _sut;
        private readonly List<FileProgress> _progressUpdates = new();
        private CancellationTokenSource _cts = null!;
        private readonly string _defaultTestFilePath = "test_retry_file.msi"; // Base path for tests not using unique files
        private List<string> _filesToCleanup = new();

        // Constants relevant to tests
        private const long DefaultChunkSize = 1 * 1024 * 1024; // 1MB chunk size

        // Use specific retry settings for these tests to verify retry counts
        // Using 1 retry means 2 total attempts (initial + 1 retry)
        private const int TestMaxRetries = 1;
        private readonly TimeSpan TestInitialDelay = TimeSpan.Zero; // Use zero delay for faster tests
        private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);

        public FileDownloader_RetryTests()
        {
            // Initialize SUT WITH the specific retry settings for these tests
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
            CleanUpFile(_defaultTestFilePath); // Clean up default path if used
        }

        [TearDown]
        public void Teardown()
        {
            _cts?.Dispose();
            // Clean up any files created during tests using ExecuteWithUniqueFileAsync
            foreach (var filePath in _filesToCleanup)
            {
                CleanUpFile(filePath);
            }
            CleanUpFile(_defaultTestFilePath); // Clean up default path again
        }

        // Helper to run tests needing unique file paths
        private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_retry_unique", string extension = ".msi")
        {
            var uniqueFilePath = $"{filePrefix}_{Guid.NewGuid()}{extension}";
            _filesToCleanup.Add(uniqueFilePath); // Track file for cleanup
            CleanUpFile(uniqueFilePath); // Ensure clean state before test
            try
            {
                await testAction(uniqueFilePath);
            }
            finally
            {
                // Teardown handles the actual cleanup
            }
        }

        // File cleanup helper
        private void CleanUpFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    Console.WriteLine($"Cleaned up test file: {filePath}");
                }
                catch (Exception ex)
                {
                    // Log warning but don't fail test run if cleanup fails
                    Console.WriteLine($"!!! WARNING: Failed to clean up test file '{filePath}': {ex.Message}");
                }
            }
        }

        // Progress handler mock
        private void HandleProgress(FileProgress progress)
        {
            _progressUpdates.Add(progress);
            // Console.WriteLine($"Progress: {progress}"); // Keep commented out unless debugging progress
        }

        // Helper to create header responses (copied from original Tests.cs)
        private HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (supportPartial) response.Headers.AcceptRanges.Add("bytes");
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentLength = contentLength;
            if (md5 != null) response.Content.Headers.ContentMD5 = md5;
            return response;
        }

        // Helper to create partial content responses (copied from original Tests.cs)
        private HttpResponseMessage CreatePartialContentResponse(byte[] fullData, long rangeFrom, long rangeTo)
        {
            if (rangeFrom >= fullData.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            rangeTo = Math.Min(rangeTo, fullData.Length - 1);
            if (rangeTo < rangeFrom) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);

            var partialData = fullData.Skip((int)rangeFrom).Take((int)(rangeTo - rangeFrom + 1)).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
            response.Content = new ByteArrayContent(partialData);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
            response.Content.Headers.ContentLength = partialData.Length;
            return response;
        }

        // Helper to generate test data
        private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();


        // =========================================
        // Retry Logic Tests
        // Extracted/adapted from original Tests.cs
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
        {
            // Tests retry on header exception, fails after TestMaxRetries
            // Arrange
            var url = "http://fail-exception.com/file.msi";
            var filePath = _defaultTestFilePath; // No file interaction expected here

            // Mock the header request to fail with 503 (retriable status), then throw an exception on retry
            _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // First call fails (retriable)
                         .ThrowsAsync(new HttpRequestException("Network unavailable")); // Second call throws

            // Act
            var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == false);
            // Verify GetHeadersAsync was called TestMaxRetries + 1 times
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
        {
            // Tests retry on header status code, succeeds on retry
            // Arrange - uses ExecuteWithUniqueFileAsync as it proceeds to download
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                var url = "http://retry-header.com/file.msi";
                int fileSize = 100;
                var testData = GenerateTestData(fileSize); // Needed for subsequent download

                // Mock header request: fail first (503 - retriable), succeed second
                _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                             .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // Fail first
                             .ReturnsAsync(CreateHeadersResponse(fileSize, false)); // Succeed second

                // Mock the subsequent full download call
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed overall
                                              // Verify GetHeadersAsync was called exactly twice (initial fail + successful retry)
                _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(2));
                _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once); // Download called once
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldFail_WhenNetworkCallFailsMoreThanMaxRetries()
        {
            // Tests exceeding max retries on header fetch (using exceptions)
            // Arrange
            var url = "http://retry-fail-max.com/file.msi";
            var filePath = _defaultTestFilePath; // No file interaction expected

            // Mock header request to throw consistently
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ThrowsAsync(new HttpRequestException("Persistent network failure"));

            // Act
            var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result, Is.False); // Should fail overall
            // Verify called initial + max retries times
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            Assert.That(File.Exists(filePath), Is.False); // No file should be created
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFailsAfterRetries()
        {
            // Tests exceeding max retries on full download (using status codes)
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://full-download-fails.com/file.msi";
                long fileSize = 1024;

                // Mock headers indicating no partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

                // Mock the full download to fail consistently with a retriable status code
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result == false); // Should fail
                _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
                // Verify called initial + max retries times
                _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
                Assert.That(File.Exists(uniqueTestFilePath) == false); // File should not exist or be cleaned up
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadThrowsNetworkErrorAfterRetries()
        {
            // Tests exceeding max retries on full download (using exceptions)
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://full-download-network-error.com/file.msi";
                long fileSize = 1024;

                // Mock headers indicating no partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

                // Mock the full download to throw an exception consistently
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .ThrowsAsync(new HttpRequestException("Network error during download"));

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result == false); // Should fail
                                              // Verify called initial + max retries times
                _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
            });
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenPartialDownloadChunkFailsAfterRetries()
        {
            // Tests exceeding max retries on partial chunk download (using status codes)
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://partial-chunk-fails.com/file.msi";
                int fileSize = (int)(DefaultChunkSize * 1.5); // Requires 2 chunks
                var testData = GenerateTestData(fileSize);

                // Mock headers indicating partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                            .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

                // Mock the first chunk successfully
                long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock the second chunk to fail consistently with a retriable status code
                long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.False); // Should fail overall
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk succeeded once
                                                                                                                             // Verify second chunk called initial + max retries times
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1));
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // Partial file should still exist
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(DefaultChunkSize)); // Size is only the first chunk
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenPartialChunkFailsOnceThenSucceeds()
        {
            // Tests retry on partial chunk failure (using exception), succeeds on retry
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://retry-partial.com/file.msi";
                int fileSize = (int)(DefaultChunkSize * 1.5); // Requires 2 chunks
                var testData = GenerateTestData(fileSize);

                // Mock headers indicating partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

                // Mock the first chunk successfully
                long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock the second chunk to fail once (throw exception), then succeed
                long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
                _mockWebCalls.SetupSequence(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                             .ThrowsAsync(new HttpRequestException("Temporary chunk error")) // Fails first time
                             .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom2, rangeTo2)); // Succeeds on retry

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed overall
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk succeeded once
                // Verify second chunk called exactly twice (initial fail + successful retry)
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(2));
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should be complete
            });
        }
    }
}