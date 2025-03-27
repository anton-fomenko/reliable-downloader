using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Net;
// Make sure your helper class namespace is accessible
// using static ReliableDownloader.Tests.FileDownloaderTestHelper; // Optional

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_HeaderTests // No inheritance needed
    {
        // Store the context returned by the helper
        private FileDownloaderTestHelper.TestContextData _context = null!;

        // Keep test-specific fields if needed
        private readonly string _defaultTestFilePath = "test_download_header.msi";

        [SetUp]
        public void Setup()
        {
            // Get the common setup from the helper
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
            // Perform additional test-specific setup (like cleaning up specific files)
            FileDownloaderTestHelper.CleanUpFile(_defaultTestFilePath);
        }

        [TearDown]
        public void Teardown()
        {
            // Perform test-specific teardown first
            FileDownloaderTestHelper.CleanUpFile(_defaultTestFilePath);
            // Call the common teardown helper
            FileDownloaderTestHelper.TeardownTestEnvironment(_context);
        }

        // =========================================
        // Header & Basic Setup Tests
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
        {
            // Arrange
            var url = "http://fail.com/file.msi";
            var filePath = _defaultTestFilePath;

            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            // Use context mock
            _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                         .ReturnsAsync(response);

            // Act
            // Use context SUT, helper for progress, context CTS
            var result = await _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

            // Assert
            Assert.That(result, Is.False);
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Once());
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestThrowsExceptionAfterRetry()
        {
            // Arrange
            var url = "http://fail-exception.com/file.msi";
            var filePath = _defaultTestFilePath;

            // Use context mock
            _context.MockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _context.Cts.Token))
                         .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                         .ThrowsAsync(new HttpRequestException("Network unavailable"));

            // Act
            var result = await _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

            // Assert
            Assert.That(result, Is.False);
            // Use static helper constant for retry count
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenContentLengthMissing()
        {
            // Arrange
            var url = "http://no-length.com/file.msi";
            var filePath = _defaultTestFilePath;

            // Use context mock
            _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                         .Returns(() => {
                             // Use static helper
                             var response = FileDownloaderTestHelper.CreateHeadersResponse(0, false);
                             response.Content.Headers.ContentLength = null; // Explicitly remove length
                             return Task.FromResult(response);
                         });

            // Act
            var result = await _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

            // Assert
            Assert.That(result, Is.False);
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Once());
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenHeaderCallFailsOnceThenSucceeds()
        {
            // Arrange - Use static helper, passing context
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                var url = "http://retry-header.com/file.msi";
                int fileSize = 100;
                // Use static helper
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.SetupSequence(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                             // Use static helper
                             .ReturnsAsync(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false));

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
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Exactly(2)); // Called twice
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Once);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldFail_WhenNetworkCallFailsMoreThanMaxRetries()
        {
            // Arrange
            var url = "http://retry-fail-max.com/file.msi";
            var filePath = _defaultTestFilePath;

            // Use context mock
            _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                         .ThrowsAsync(new HttpRequestException("Persistent network failure"));

            // Act
            var result = await _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

            // Assert
            Assert.That(result, Is.False);
            // Use static helper constant
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
            Assert.That(File.Exists(filePath), Is.False);
        }
    }
}