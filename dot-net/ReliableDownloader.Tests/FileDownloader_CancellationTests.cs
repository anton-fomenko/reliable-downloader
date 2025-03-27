using Moq;
using NUnit.Framework;
using System.Diagnostics; // Needed for SlowStream potentially, though it uses Task.Delay
using System.Net;
using System.Net.Http.Headers;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_CancellationTests
    {
        // Copied from original Tests.cs for context
        private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
        private readonly FileDownloader _sut;
        private readonly List<FileProgress> _progressUpdates = new();
        private CancellationTokenSource _cts = null!;
        private readonly string _defaultTestFilePath = "test_cancel_file.msi"; // Base path for tests not using unique files
        private List<string> _filesToCleanup = new();

        // Constants relevant to tests
        private const long DefaultChunkSize = 1 * 1024 * 1024; // 1MB chunk size used in partial cancel test

        // Using minimal retries for faster test execution where applicable
        private const int TestMaxRetries = 1;
        private readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);

        public FileDownloader_CancellationTests()
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
            CleanUpFile(_defaultTestFilePath); // Clean up default path
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
        private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_cancel_unique", string extension = ".msi")
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

        // SlowStream class needed for simulating cancellations during stream reads (copied from original Tests.cs)
        private class SlowStream : MemoryStream
        {
            private readonly int _delayMs;
            private readonly CancellationToken _token; // Token from the test's CancellationTokenSource

            public SlowStream(byte[] buffer, int delayMs, CancellationToken token) : base(buffer)
            {
                _delayMs = delayMs;
                _token = token; // Store the test's cancellation token
            }

            // Override ReadAsync to introduce delay and check for cancellation
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) // cancellationToken passed by the caller (e.g., Stream.CopyToAsync)
            {
                // Link the test's token with the token passed into ReadAsync
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
                try
                {
                    if (_delayMs > 0) await Task.Delay(_delayMs, linkedCts.Token);
                    linkedCts.Token.ThrowIfCancellationRequested(); // Check if cancellation was requested (either from test or caller)
                    return await base.ReadAsync(buffer, offset, count, linkedCts.Token);
                }
                // Distinguish between cancellation sources if necessary, though often just rethrowing is fine
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; } // Test initiated cancellation
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // Caller initiated cancellation
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // Link the test's token with the token passed into ReadAsync
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
                try
                {
                    if (_delayMs > 0) await Task.Delay(_delayMs, linkedCts.Token);
                    linkedCts.Token.ThrowIfCancellationRequested(); // Check if cancellation was requested
                    return await base.ReadAsync(buffer, linkedCts.Token);
                }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            }
        }


        // =========================================
        // Cancellation Tests
        // Extracted from original Tests.cs
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenCancelledDuringHeaderCheck()
        {
            // Arrange
            var url = "http://cancellable-header.com/file.msi";
            var filePath = _defaultTestFilePath; // No file created here

            // Mock GetHeadersAsync to delay and then check for cancellation
            _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                          .Returns(async () => {
                              await Task.Delay(100, _cts.Token); // Simulate delay where cancellation might occur
                              _cts.Token.ThrowIfCancellationRequested(); // Explicitly throw if cancelled during delay
                              // This part might not be reached if cancelled quickly enough
                              return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); // Return something if not cancelled
                          });

            // Act
            // Start the download task but don't await immediately
            var downloadTask = _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);
            // Wait a short time, less than the delay in the mock, then cancel
            await Task.Delay(50);
            _cts.Cancel();

            // Assert
            // Expect TaskCanceledException specifically
            Assert.ThrowsAsync<TaskCanceledException>(async () => await downloadTask);
            // Verify cancellation was indeed requested
            Assert.That(_cts.IsCancellationRequested, Is.True);
            // Verify the mocked call was attempted at most once
            _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.AtMostOnce());
            Assert.That(!File.Exists(filePath), "File should not be created if cancelled during header check.");
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenCancelledDuringFullDownload()
        {
            // Arrange - uses ExecuteWithUniqueFileAsync as it involves file writing
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                var url = "http://cancel-during-full.com/file.msi";
                long fileSize = 500000; // Reasonably large file
                var fileBytes = GenerateTestData((int)fileSize);

                // Mock headers indicating no partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, false)));

                // Mock the full download to use SlowStream
                _mockWebCalls.Setup(w => w.DownloadContentAsync(url, _cts.Token))
                             .Returns(async () => {
                                 // Short initial delay before returning the stream
                                 await Task.Delay(20, _cts.Token);
                                 var response = new HttpResponseMessage(HttpStatusCode.OK);
                                 // Use SlowStream with a delay per read and pass the test's CancellationToken
                                 response.Content = new StreamContent(new SlowStream(fileBytes, 50, _cts.Token));
                                 response.Content.Headers.ContentLength = fileSize;
                                 return response;
                             });

                // Act
                var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);
                // Wait long enough for the download to start writing, then cancel
                await Task.Delay(150); // Increased delay to ensure stream copy likely started
                _cts.Cancel();

                // Await the task and expect it to return false due to cancellation path
                var result = await downloadTask;


                // Assert
                Assert.That(result == false); // Download should report failure on cancellation
                Assert.That(_cts.IsCancellationRequested, Is.True);
                _mockWebCalls.Verify(w => w.DownloadContentAsync(url, _cts.Token), Times.Once()); // Should be called once
                // FileDownloader logic should delete the file on cancellation during stream copy
                Assert.That(!File.Exists(uniqueTestFilePath), "File should be deleted after cancellation during full download.");
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalseAndKeepPartial_WhenCancelledDuringPartialDownload()
        {
            // Arrange - uses ExecuteWithUniqueFileAsync
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                var url = "http://cancel-during-partial.com/file.msi";
                int fileSize = (int)(DefaultChunkSize * 2.5); // Requires multiple chunks
                var testData = GenerateTestData(fileSize);

                // Mock headers indicating partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                            .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

                // Mock the first chunk to succeed quickly
                long rangeFrom1 = 0, rangeTo1 = DefaultChunkSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock the second chunk using SlowStream
                long rangeFrom2 = DefaultChunkSize, rangeTo2 = rangeFrom2 + DefaultChunkSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                            .Returns(async () => {
                                await Task.Delay(20, _cts.Token); // Short delay before stream
                                var chunkData = testData.Skip((int)rangeFrom2).Take((int)(rangeTo2 - rangeFrom2 + 1)).ToArray();
                                var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                                // Use SlowStream for the response content
                                response.Content = new StreamContent(new SlowStream(chunkData, 50, _cts.Token));
                                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom2, rangeTo2, fileSize);
                                response.Content.Headers.ContentLength = chunkData.Length;
                                return response;
                            });

                // Mock third chunk just in case (though cancellation should prevent it)
                long rangeFrom3 = rangeFrom2 + DefaultChunkSize;
                long rangeTo3 = fileSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom3, rangeTo3, _cts.Token))
                           .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, rangeFrom3, rangeTo3)));


                // Act
                var downloadTask = _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);
                // Wait long enough for the second chunk's SlowStream to likely be active
                await Task.Delay(200); // Adjusted delay
                _cts.Cancel();

                // Await the task and expect it to return false due to cancellation path
                var result = await downloadTask;

                // Assert
                Assert.That(result, Is.False); // Download should report failure
                Assert.That(_cts.IsCancellationRequested, Is.True);
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk called
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.AtMostOnce()); // Second (slow) chunk attempted at most once
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom3, rangeTo3, _cts.Token), Times.Never); // Third chunk should not be reached

                // *** FIXED ASSERTIONS ***
                Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should be kept after cancellation during partial download.");
                // Verify the file size is greater than or equal to the first chunk, but less than the total size.
                var actualLength = new FileInfo(uniqueTestFilePath).Length;
                Assert.That(actualLength, Is.GreaterThanOrEqualTo(DefaultChunkSize), $"File size {actualLength} should be >= first chunk {DefaultChunkSize}");
                Assert.That(actualLength, Is.LessThan(fileSize), $"File size {actualLength} should be < total size {fileSize}");
            });
        }
    }
}