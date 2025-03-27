using Moq;
using NUnit.Framework;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
// Make sure your helper class namespace is accessible
// using static ReliableDownloader.Tests.FileDownloaderTestHelper; // Optional: if you want to call helpers directly without class name

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_CancellationTests // No base class inheritance
    {
        // Store the context returned by the helper
        private FileDownloaderTestHelper.TestContextData _context = null!;

        // Keep test-specific fields if needed
        private readonly string _defaultTestFilePath = "test_cancel_file.msi";

        [SetUp]
        public void Setup()
        {
            // Get the common setup from the helper
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
            // Perform any additional test-specific setup
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
        // Cancellation Tests
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldThrowTaskCanceled_WhenCancelledDuringHeaderCheck()
        {
            // Arrange
            var url = "http://cancellable-header.com/file.msi";
            var filePath = _defaultTestFilePath;

            // Use mock from context
            _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()))
                          .Returns(async (string u, CancellationToken ct) => {
                              // Use CTS from context
                              using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_context.Cts.Token, ct);
                              await Task.Delay(100, linkedCts.Token);
                              linkedCts.Token.ThrowIfCancellationRequested();
                              return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                          });

            // Act
            // Use SUT and HandleProgress via helper from context
            var downloadTask = _context.Sut.TryDownloadFile(url, filePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);
            await Task.Delay(50);
            _context.Cts.Cancel(); // Use CTS from context

            // Assert
            Assert.ThrowsAsync<TaskCanceledException>(async () => await downloadTask);
            Assert.That(_context.Cts.IsCancellationRequested, Is.True);
            _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()), Times.AtMostOnce());
            Assert.That(!File.Exists(filePath), "File should not be created if cancelled during header check.");
        }


        [Test]
        public async Task TryDownloadFile_ShouldReturnFalseAndCleanup_WhenCancelledDuringFullDownload()
        {
            // Arrange - use static helper, passing context
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                var url = "http://cancel-during-full.com/file.msi";
                long fileSize = 500000;
                // Use static helper
                var fileBytes = FileDownloaderTestHelper.GenerateTestData((int)fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()))
                             // Use static helper
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, It.IsAny<CancellationToken>()))
                              .Returns(async (string u, CancellationToken ct) => {
                                  // Use CTS from context
                                  using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_context.Cts.Token, ct);
                                  await Task.Delay(20, linkedCts.Token);
                                  linkedCts.Token.ThrowIfCancellationRequested();
                                  var response = new HttpResponseMessage(HttpStatusCode.OK);
                                  // Use static nested class SlowStream via helper, passing context CTS
                                  response.Content = new StreamContent(new FileDownloaderTestHelper.SlowStream(fileBytes, 50, _context.Cts.Token));
                                  response.Content.Headers.ContentLength = fileSize;
                                  return response;
                              });

                // Act
                var downloadTask = _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);
                await Task.Delay(150);
                _context.Cts.Cancel(); // Use context CTS

                var result = await downloadTask;

                // Assert
                Assert.That(result, Is.False);
                Assert.That(_context.Cts.IsCancellationRequested, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, It.IsAny<CancellationToken>()), Times.Once());
                Assert.That(!File.Exists(uniqueTestFilePath), "File should be deleted after cancellation during full download.");
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalseAndKeepPartial_WhenCancelledDuringPartialDownload()
        {
            // Arrange - use static helper, passing context
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                var url = "http://cancel-during-partial.com/file.msi";
                // Use constant from static helper
                int fileSize = (int)(FileDownloaderTestHelper.DefaultChunkSize * 2.5);
                // Use static helper
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()))
                            // Use static helper
                            .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                // Mock chunk 1
                long rangeFrom1 = 0, rangeTo1 = FileDownloaderTestHelper.DefaultChunkSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, It.IsAny<CancellationToken>()))
                             // Use static helper
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock chunk 2 (SlowStream)
                long rangeFrom2 = FileDownloaderTestHelper.DefaultChunkSize;
                long rangeTo2 = rangeFrom2 + FileDownloaderTestHelper.DefaultChunkSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, It.IsAny<CancellationToken>()))
                            .Returns(async (string u, long f, long t, CancellationToken ct) => {
                                // Use context CTS
                                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_context.Cts.Token, ct);
                                await Task.Delay(20, linkedCts.Token);
                                linkedCts.Token.ThrowIfCancellationRequested();
                                var chunkData = testData.Skip((int)rangeFrom2).Take((int)(rangeTo2 - rangeFrom2 + 1)).ToArray();
                                var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                                // Use static nested class SlowStream via helper, passing context CTS
                                response.Content = new StreamContent(new FileDownloaderTestHelper.SlowStream(chunkData, 50, _context.Cts.Token));
                                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom2, rangeTo2, fileSize);
                                response.Content.Headers.ContentLength = chunkData.Length;
                                return response;
                            });

                // Mock chunk 3
                long rangeFrom3 = rangeFrom2 + FileDownloaderTestHelper.DefaultChunkSize;
                long rangeTo3 = fileSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom3, rangeTo3, It.IsAny<CancellationToken>()))
                           // Use static helper
                           .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom3, rangeTo3)));


                // Act
                var downloadTask = _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);
                await Task.Delay(200);
                _context.Cts.Cancel(); // Use context CTS

                var result = await downloadTask;

                // Assert
                Assert.That(result, Is.False);
                Assert.That(_context.Cts.IsCancellationRequested, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, It.IsAny<CancellationToken>()), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, It.IsAny<CancellationToken>()), Times.AtMostOnce());
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom3, rangeTo3, It.IsAny<CancellationToken>()), Times.Never);

                Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should be kept after cancellation during partial download.");
                var actualLength = new FileInfo(uniqueTestFilePath).Length;
                Assert.That(actualLength, Is.GreaterThanOrEqualTo(FileDownloaderTestHelper.DefaultChunkSize));
                Assert.That(actualLength, Is.LessThan(fileSize));
            });
        }
    }
}