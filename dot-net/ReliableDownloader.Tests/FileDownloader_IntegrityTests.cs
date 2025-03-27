using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy; // Needed for CollectionAssert if using older NUnit structure
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography; // Required for MD5 generation in tests

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_IntegrityTests
    {
        // Copied from original Tests.cs for context
        private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
        private readonly FileDownloader _sut;
        private readonly List<FileProgress> _progressUpdates = new();
        private CancellationTokenSource _cts = null!;
        private List<string> _filesToCleanup = new();

        // Using minimal retries for faster test execution where applicable
        private const int TestMaxRetries = 1;
        private readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);

        public FileDownloader_IntegrityTests()
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
            // No default path cleanup needed as ExecuteWithUniqueFileAsync is used
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
        }

        // Helper to run tests needing unique file paths
        private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_integrity_file", string extension = ".msi")
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
        // Now includes MD5 parameter generation within the helper for these tests
        private HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (supportPartial) response.Headers.AcceptRanges.Add("bytes");
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentLength = contentLength;
            if (md5 != null) response.Content.Headers.ContentMD5 = md5; // Set the Content-MD5 header
            return response;
        }

        // Helper to generate test data
        private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();

        // Helper function specific to these tests to compute MD5
        private byte[] ComputeMd5(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                return md5.ComputeHash(data);
            }
        }


        // =========================================
        // MD5 Integrity Tests
        // Extracted from original Tests.cs
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
                byte[] correctMd5 = ComputeMd5(testData); // Calculate correct MD5

                // Mock headers with partial support OFF and CORRECT MD5
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false, correctMd5)));

                // Mock the full download content request
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 // Note: The MD5 header is on the HEAD response, not necessarily the GET response content headers
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed overall
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should match
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
                byte[] correctMd5 = ComputeMd5(testData);
                // Create an incorrect MD5 hash (e.g., by flipping bits)
                byte[] incorrectMd5 = correctMd5.Select(b => (byte)(~b)).ToArray();

                // Mock headers with partial support OFF and INCORRECT MD5
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false, incorrectMd5)));

                // Mock the full download content request (which delivers the correct data)
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });


                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.False); // Should fail due to mismatch
                // Verify the file was deleted by the integrity check failure logic
                Assert.That(File.Exists(uniqueTestFilePath), Is.False, "File should be deleted after MD5 mismatch.");
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

                // Mock headers with partial support OFF and NO MD5 header
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false, md5: null)));

                // Mock the full download content request
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed as check is skipped
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should match
            });
        }
    }
}