using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy; // Needed for CollectionAssert if using older NUnit structure
using System.Net;
using System.Net.Http.Headers;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_PartialDownloadTests
    {
        // Copied from original Tests.cs for context
        private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
        private readonly FileDownloader _sut;
        private readonly List<FileProgress> _progressUpdates = new();
        private CancellationTokenSource _cts = null!;
        private List<string> _filesToCleanup = new();

        // Constants relevant to partial downloads
        private const long DefaultChunkSize = 1 * 1024 * 1024; // 1MB chunk size used in tests

        // Using minimal retries for faster test execution where applicable
        private const int TestMaxRetries = 1;
        private readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        private readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);

        public FileDownloader_PartialDownloadTests()
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
        private async Task ExecuteWithUniqueFileAsync(Func<string, Task> testAction, string filePrefix = "test_partial_file", string extension = ".msi")
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
            // Ensure rangeTo is not less than rangeFrom
            if (rangeTo < rangeFrom)
            {
                // This might happen if rangeFrom is exactly fullData.Length
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            var partialData = fullData.Skip((int)rangeFrom).Take((int)(rangeTo - rangeFrom + 1)).ToArray();

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
            response.Content = new ByteArrayContent(partialData);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
            response.Content.Headers.ContentLength = partialData.Length; // Crucial: Length is of the partial content
            return response;
        }


        // Helper to generate test data
        private byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();

        // =========================================
        // Partial Download Tests
        // Extracted from original Tests.cs
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldAttemptPartialDownload_WhenPartialSupportedAndHeadersPresent()
        {
            // This test verifies the initial attempt branch, including a single chunk success
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://partial-support.com/file.msi";
                int fileSize = 1024; // Smaller than DefaultChunkSize, so one chunk expected
                var testData = GenerateTestData(fileSize);

                // Mock headers indicating partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

                // Mock the partial download request for the *entire file* (since it's one chunk)
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, 0, fileSize - 1)));

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed
                _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _cts.Token), Times.Once); // Verify partial called once
                _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); // Verify full download NOT called
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should match
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldPerformPartialDownload_InMultipleChunks_WhenSupported()
        {
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://multi-partial-support.com/file.msi";
                // File size requiring multiple chunks (e.g., 2.5 chunks)
                int fileSize = (int)(DefaultChunkSize * 2.5);
                var testData = GenerateTestData(fileSize);

                // Mock headers indicating partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

                // Mock the partial download calls for each expected chunk
                long currentPos = 0;
                int expectedCalls = 0;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                    long rangeFrom = currentPos;
                    long rangeTo = rangeFrom + bytesToDownload - 1;
                    // Important: Capture loop variables for the lambda
                    long currentRangeFrom = rangeFrom;
                    long currentRangeTo = rangeTo;
                    _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, currentRangeFrom, currentRangeTo, _cts.Token))
                                 .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, currentRangeFrom, currentRangeTo)));
                    currentPos += bytesToDownload;
                    expectedCalls++;
                }

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed
                _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once);
                _mockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); // Full download not called
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls)); // Verify correct number of partial calls

                // Explicitly verify each range call was made once
                currentPos = 0;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                    _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token), Times.Once);
                    currentPos += bytesToDownload;
                }

                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should match
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldResumePartialDownload_WhenFileExists()
        {
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://resume-partial.com/file.msi";
                // File size requiring multiple chunks
                int fileSize = (int)(DefaultChunkSize * 1.5);
                var testData = GenerateTestData(fileSize);
                // Create a partial file (e.g., half of the first chunk)
                int existingSize = (int)(DefaultChunkSize / 2);
                var existingData = testData.Take(existingSize).ToArray();
                await File.WriteAllBytesAsync(uniqueTestFilePath, existingData);

                // Mock headers indicating partial support
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(fileSize, true)));

                // Mock the partial download calls for the REMAINING chunks/bytes
                long currentPos = existingSize; // Start mocking from where the existing file left off
                int expectedCalls = 0;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                    long rangeFrom = currentPos;
                    long rangeTo = rangeFrom + bytesToDownload - 1;
                    long currentRangeFrom = rangeFrom; // Capture loop variables
                    long currentRangeTo = rangeTo;
                    _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, currentRangeFrom, currentRangeTo, _cts.Token))
                                 .Returns(() => Task.FromResult(CreatePartialContentResponse(testData, currentRangeFrom, currentRangeTo)));
                    currentPos += bytesToDownload;
                    expectedCalls++;
                }

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed
                // Verify that the download did NOT start from byte 0
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
                // Verify the correct number of partial calls were made for the remaining bytes
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _cts.Token), Times.Exactly(expectedCalls));

                // Explicitly verify the ranges that *should* have been called
                currentPos = existingSize;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, DefaultChunkSize);
                    _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _cts.Token), Times.Once);
                    currentPos += bytesToDownload;
                }

                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should be complete
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenPartialDownloadChunkFailsAfterRetries()
        {
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

                // Mock the second chunk to fail consistently
                long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.False); // Should fail overall
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk succeeded once
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Second chunk failed initial + retries
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // Partial file should still exist
                // Verify the size is only the first chunk
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(DefaultChunkSize));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenPartialChunkFailsOnceThenSucceeds()
        {
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

                // Mock the second chunk to fail once, then succeed
                long rangeFrom2 = DefaultChunkSize, rangeTo2 = fileSize - 1;
                _mockWebCalls.SetupSequence(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token))
                             .ThrowsAsync(new HttpRequestException("Temporary chunk error")) // Fails first time
                             .ReturnsAsync(CreatePartialContentResponse(testData, rangeFrom2, rangeTo2)); // Succeeds on retry

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed overall
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _cts.Token), Times.Once); // First chunk succeeded once
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _cts.Token), Times.Exactly(TestMaxRetries + 1)); // Second chunk called twice (fail + success)
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should be complete
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldRestartDownload_WhenExistingFileIsLargerThanTotalSize_AndPartialSupported()
        {
            // This verifies the restart logic even when partial is supported, if the local file is invalidly large
            await ExecuteWithUniqueFileAsync(async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://local-larger-partial-ok.com/file.msi";
                int serverFileSize = 1000;
                int localFileSize = 1500; // Existing file is larger
                var serverData = GenerateTestData(serverFileSize);
                var localData = GenerateTestData(localFileSize);
                await File.WriteAllBytesAsync(uniqueTestFilePath, localData); // Create oversized local file

                // Mock headers indicating partial support IS available, but size mismatch should trigger restart
                _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                             .Returns(() => Task.FromResult(CreateHeadersResponse(serverFileSize, true)));

                // Mock the partial download for the *entire file* which should happen after the restart
                long rangeFrom = 0, rangeTo = serverFileSize - 1;
                _mockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token))
                             .Returns(() => Task.FromResult(CreatePartialContentResponse(serverData, rangeFrom, rangeTo)));

                // Act
                var result = await _sut.TryDownloadFile(url, uniqueTestFilePath, HandleProgress, _cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed eventually
                // Verify partial download was called once (for the *whole* range after restart)
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _cts.Token), Times.Once);
                // Verify no *other* partial calls (e.g., attempting to resume from 1500) were made.
                _mockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.Is<long>(l => l != 0), It.IsAny<long>(), _cts.Token), Times.Never);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // File should exist
                CollectionAssert.AreEqual(serverData, await File.ReadAllBytesAsync(uniqueTestFilePath)); // Content should be the server's data
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(serverFileSize)); // Size should match server
            });
        }
    }
}