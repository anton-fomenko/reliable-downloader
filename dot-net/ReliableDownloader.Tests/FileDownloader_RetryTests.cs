using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy; // Needed for CollectionAssert
using System.Net;

namespace ReliableDownloader.Tests
{
    /// <summary>
    /// Contains tests specifically focused on the FileDownloader's retry logic
    /// across different download phases (headers, full, partial).
    /// Other test files may implicitly involve retries, but tests here verify
    /// the retry mechanism itself (e.g., number of attempts, success after failure).
    /// </summary>
    [TestFixture]
    public class FileDownloader_RetryTests
    {
        private FileDownloaderTestHelper.TestContextData _context = null!;
        private readonly string _defaultTestFilePath = "test_retry_file.msi";

        [SetUp]
        public void Setup()
        {
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
            FileDownloaderTestHelper.CleanUpFile(_defaultTestFilePath);
        }

        [TearDown]
        public void Teardown()
        {
            FileDownloaderTestHelper.CleanUpFile(_defaultTestFilePath);
            FileDownloaderTestHelper.TeardownTestEnvironment(_context);
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
        {
            // Tests retry on header exception, fails after TestMaxRetries
            // Arrange
            var url = "http://fail-exception.com/file.msi";
            var filePath = _defaultTestFilePath; // Using default path

            _context.MockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _context.Cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                         .ThrowsAsync(new HttpRequestException("Network unavailable"));

            // Act
            var result = await _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

            // Assert
            Assert.That(result, Is.False);
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
        {
            // Tests retry on header status code, succeeds on retry
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                var url = "http://retry-header.com/file.msi";
                int fileSize = 100;
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) // Fail first time
                             .ReturnsAsync(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)); // Succeed on retry

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Exactly(2)); // Called twice (initial + 1 retry)
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Once);
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
            var filePath = _defaultTestFilePath; // Using default path

            _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                         .ThrowsAsync(new HttpRequestException("Persistent network failure")); // Fail consistently

            // Act
            var result = await _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

            // Assert
            Assert.That(result, Is.False);
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
            Assert.That(File.Exists(filePath), Is.False);
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFailsAfterRetries()
        {
            // Tests exceeding max retries on full download (using status codes)
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://full-download-fails.com/file.msi";
                long fileSize = 1024;

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))); // Fail consistently

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.False);
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
                Assert.That(File.Exists(uniqueTestFilePath), Is.False);
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadThrowsNetworkErrorAfterRetries()
        {
            // Tests exceeding max retries on full download (using exceptions)
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://full-download-network-error.com/file.msi";
                long fileSize = 1024;

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .ThrowsAsync(new HttpRequestException("Network error during download")); // Fail consistently

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.False);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
            });
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenPartialDownloadChunkFailsAfterRetries()
        {
            // Tests exceeding max retries on partial chunk download (using status codes)
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://partial-chunk-fails.com/file.msi";
                int fileSize = (int)(FileDownloaderTestHelper.DefaultTestChunkSize * 1.5); // Requires 2 chunks
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                            .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                // Mock the first chunk successfully
                long rangeFrom1 = 0, rangeTo1 = FileDownloaderTestHelper.DefaultTestChunkSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock the second chunk to fail consistently
                long rangeFrom2 = FileDownloaderTestHelper.DefaultTestChunkSize, rangeTo2 = fileSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))); // Fail consistently

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.False);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // Partial file should still exist after failed chunk retry
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(FileDownloaderTestHelper.DefaultTestChunkSize)); // Only first chunk completed
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenPartialChunkFailsOnceThenSucceeds()
        {
            // Tests retry on partial chunk failure (using exception), succeeds on retry
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://retry-partial.com/file.msi";
                int fileSize = (int)(FileDownloaderTestHelper.DefaultTestChunkSize * 1.5); // Requires 2 chunks
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                // Mock the first chunk successfully
                long rangeFrom1 = 0, rangeTo1 = FileDownloaderTestHelper.DefaultTestChunkSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock the second chunk to fail once (exception), then succeed
                long rangeFrom2 = FileDownloaderTestHelper.DefaultTestChunkSize, rangeTo2 = fileSize - 1;
                _context.MockWebCalls.SetupSequence(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token))
                             .ThrowsAsync(new HttpRequestException("Temporary chunk error")) // Fails first time
                             .ReturnsAsync(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom2, rangeTo2)); // Succeeds on retry

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token), Times.Exactly(2)); // Called twice (initial + 1 retry)
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }
    }
}