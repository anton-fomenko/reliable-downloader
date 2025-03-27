using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Net;
using System.Net.Http.Headers;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_HeaderTests
    {
        // Copied from original Tests.cs for context within this specific file
        private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
        private readonly FileDownloader _sut;
        private readonly List<FileProgress> _progressUpdates = new();
        private CancellationTokenSource _cts = null!;
        private readonly string _defaultTestFilePath = "test_download_header.msi"; // Unique name to avoid clashes if run together
        private List<string> _filesToCleanup = new();

        // Using minimal retries for faster test execution where applicable
        private const int TestMaxRetries = 1;
        private readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);

        public FileDownloader_HeaderTests()
        {
            // Initialize SUT with specific retry settings for tests
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
            CleanUpFile(_defaultTestFilePath); // Clean up default path just in case
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
            CleanUpFile(_defaultTestFilePath); // Final cleanup of default path
        }

        // Helper to run tests needing unique file paths
        private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_header_file", string extension = ".msi")
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
            // Need content object to set headers, even if empty for HEAD request simulation
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentLength = contentLength;
            if (md5 != null) response.Content.Headers.ContentMD5 = md5;
            return response;
        }

        // Helper to generate test data (needed for the retry success test)
        private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();


        // =========================================
        // Header & Basic Setup Tests
        // Extracted from original Tests.cs
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
        {
            // Arrange
            var url = "http://fail.com/file.msi";
            var filePath = _defaultTestFilePath; // No file interaction expected here

            // Mock the header request to return a non-success code
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(response);

            // Act
            var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == false);
            // Verify GetHeadersAsync was called once (404 is not typically retried by the current logic)
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once());
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
        {
            // Arrange
            var url = "http://fail-exception.com/file.msi";
            var filePath = _defaultTestFilePath; // No file interaction expected here

            // Mock the header request to fail with 503, then throw an exception on retry
            _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // First call fails (retriable)
                         .ThrowsAsync(new HttpRequestException("Network unavailable")); // Second call throws

            // Act
            var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == false);
            // Verify GetHeadersAsync was called twice (initial + 1 retry)
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1));
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
        {
            // Arrange
            var url = "http://no-length.com/file.msi";
            var filePath = _defaultTestFilePath; // No file interaction expected here

            // Mock header request with OK status but missing Content-Length
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                         .Returns(() => {
                             var response = new HttpResponseMessage(HttpStatusCode.OK);
                             // Simulate content object existing but without ContentLength header set
                             response.Content = new ByteArrayContent(Array.Empty<byte>());
                             response.Content.Headers.ContentLength = null; // Explicitly null
                             return Task.FromResult(response);
                         });

            // Act
            var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

            // Assert
            Assert.That(result == false);
            // Verify GetHeadersAsync was called once (missing length is not retried)
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
        {
            // Arrange - uses ExecuteWithUniqueFileAsync as it proceeds to download
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                var url = "http://retry-header.com/file.msi";
                int fileSize = 100;
                var testData = GenerateTestData(fileSize); // Needed for subsequent download

                // Mock header request: fail first (503), succeed second
                _mockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _cts.Token))
                             .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // Fail first
                             .ReturnsAsync(CreateHeadersResponse(fileSize, false)); // Succeed second

                // Mock the subsequent full download call (assuming no partial support from header response)
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
                _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(2)); // Header called twice
                _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once); // Download called once
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // File content should match
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldFail_WhenNetworkCallFailsMoreThanMaxRetries()
        {
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
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Called initial + max retries
            Assert.That(File.Exists(filePath), Is.False); // No file should be created
        }
    }
}