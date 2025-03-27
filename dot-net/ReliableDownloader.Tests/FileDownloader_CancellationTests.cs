using Moq;
using NUnit.Framework;
using System.Net;
using System.Net.Http.Headers;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_CancellationTests
    {
        private FileDownloaderTestHelper.TestContextData _context = null!;

        [SetUp]
        public void Setup()
        {
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
            // Clean up any potential leftovers before each test
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), $"{FileDownloaderTestHelper.DefaultTestFilePathPrefix}*"))
            {
                FileDownloaderTestHelper.CleanUpFile(file);
            }
        }

        [TearDown]
        public void Teardown()
        {
            FileDownloaderTestHelper.TeardownTestEnvironment(_context);
        }

        [Test]
        public async Task TryDownloadFile_ShouldThrowTaskCanceled_WhenCancelledDuringHeaderCheck()
        {
            // Arrange
            var url = "http://cancellable-header.com/file.msi";

            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()))
                              .Returns(async (string u, CancellationToken ct) => {
                                  // Use CTS from context
                                  using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_context.Cts.Token, ct);
                                  try
                                  {
                                      await Task.Delay(150, linkedCts.Token); // Increased delay slightly
                                      linkedCts.Token.ThrowIfCancellationRequested();
                                      // Return a response even if cancelled just after delay, ExecuteWithRetry handles it
                                      return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                                  }
                                  catch (OperationCanceledException)
                                  {
                                      // Simulate Task.Delay throwing OCE which ExecuteWithRetryAsync catches and re-throws
                                      throw;
                                  }
                              });

                // Act
                var downloadTask = _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Cancel shortly after starting the operation
                await Task.Delay(50);
                _context.Cts.Cancel();

                // Assert
                // Expect TaskCanceledException (or OperationCanceledException) to be thrown
                Assert.ThrowsAsync<TaskCanceledException>(async () => await downloadTask);
                Assert.That(_context.Cts.IsCancellationRequested, Is.True);
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()), Times.AtMostOnce()); // Might not even complete one call
                Assert.That(!File.Exists(uniqueTestFilePath), "File should not exist if cancelled during header check.");

            }, filePrefix: "test_cancel_hdr_"); // Use unique prefix
        }


        [Test]
        public async Task TryDownloadFile_ShouldThrowTaskCanceledAndCleanup_WhenCancelledDuringFullDownload()
        {
            // Arrange
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                var url = "http://cancel-during-full.com/file.msi";
                long fileSize = 500000;
                var fileBytes = FileDownloaderTestHelper.GenerateTestData((int)fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, It.IsAny<CancellationToken>()))
                              .Returns(async (string u, CancellationToken ct) => {
                                  using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_context.Cts.Token, ct);
                                  try
                                  {
                                      await Task.Delay(20, linkedCts.Token); // Small initial delay
                                      linkedCts.Token.ThrowIfCancellationRequested();
                                      var response = new HttpResponseMessage(HttpStatusCode.OK);
                                      // Use SlowStream with test's CancellationToken
                                      response.Content = new StreamContent(new FileDownloaderTestHelper.SlowStream(fileBytes, 50, _context.Cts.Token));
                                      response.Content.Headers.ContentLength = fileSize;
                                      return response;
                                  }
                                  catch (OperationCanceledException)
                                  {
                                      throw; // Ensure OCE propagates if delay/stream is cancelled
                                  }
                              });

                // Act
                var downloadTask = _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Wait long enough for the download to start and hit the SlowStream delay
                await Task.Delay(150);
                _context.Cts.Cancel();

                // Assert
                Assert.ThrowsAsync<TaskCanceledException>(async () => await downloadTask);

                // Verify state *after* confirming the exception was thrown
                Assert.That(_context.Cts.IsCancellationRequested, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, It.IsAny<CancellationToken>()), Times.Once); // Should be called once
                // FileDownloader should delete the file when cancelling a full download
                Assert.That(!File.Exists(uniqueTestFilePath), "File should be deleted after cancellation during full download.");

            }, filePrefix: "test_cancel_full_");
        }

        [Test]
        public async Task TryDownloadFile_ShouldThrowTaskCanceledAndKeepPartial_WhenCancelledDuringPartialDownload()
        {
            // Arrange
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                var url = "http://cancel-during-partial.com/file.msi";
                // Use helper constant, ensure multiple chunks
                int fileSize = (int)(FileDownloaderTestHelper.DefaultTestChunkSize * 2.5);
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, It.IsAny<CancellationToken>()))
                            .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                // Mock chunk 1 (downloads successfully)
                long rangeFrom1 = 0;
                long rangeTo1 = FileDownloaderTestHelper.DefaultTestChunkSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, It.IsAny<CancellationToken>()))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom1, rangeTo1)));

                // Mock chunk 2 (uses SlowStream, will be cancelled during this chunk)
                long rangeFrom2 = FileDownloaderTestHelper.DefaultTestChunkSize;
                long rangeTo2 = rangeFrom2 + FileDownloaderTestHelper.DefaultTestChunkSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, It.IsAny<CancellationToken>()))
                           .Returns(async (string u, long f, long t, CancellationToken ct) => {
                               using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_context.Cts.Token, ct);
                               try
                               {
                                   await Task.Delay(20, linkedCts.Token); // Small initial delay for chunk
                                   linkedCts.Token.ThrowIfCancellationRequested();
                                   var chunkData = testData.Skip((int)rangeFrom2).Take((int)(rangeTo2 - rangeFrom2 + 1)).ToArray();
                                   var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                                   // Use SlowStream with the test's CancellationToken
                                   response.Content = new StreamContent(new FileDownloaderTestHelper.SlowStream(chunkData, 50, _context.Cts.Token));
                                   response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom2, rangeTo2, fileSize);
                                   response.Content.Headers.ContentLength = chunkData.Length;
                                   return response;
                               }
                               catch (OperationCanceledException)
                               {
                                   throw; // Ensure OCE propagates
                               }
                           });

                // Mock chunk 3 (shouldn't be reached)
                long rangeFrom3 = rangeFrom2 + FileDownloaderTestHelper.DefaultTestChunkSize;
                long rangeTo3 = fileSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom3, rangeTo3, It.IsAny<CancellationToken>()))
                           .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, rangeFrom3, rangeTo3)));


                // Act
                var downloadTask = _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Wait long enough for chunk 1 to finish and chunk 2's SlowStream to be active
                await Task.Delay(250);
                _context.Cts.Cancel();

                // Assert
                Assert.ThrowsAsync<TaskCanceledException>(async () => await downloadTask);

                // Verify state *after* confirming the exception was thrown
                Assert.That(_context.Cts.IsCancellationRequested, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, It.IsAny<CancellationToken>()), Times.Once);
                // Verify chunk 2 was attempted
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
                // Verify chunk 3 was never called
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom3, rangeTo3, It.IsAny<CancellationToken>()), Times.Never);

                // FileDownloader should keep the partial file when cancelling a partial download
                Assert.That(File.Exists(uniqueTestFilePath), Is.True, "Partial file should be kept after cancellation during partial download.");
                var actualLength = new FileInfo(uniqueTestFilePath).Length;
                Assert.That(actualLength, Is.GreaterThanOrEqualTo(FileDownloaderTestHelper.DefaultTestChunkSize), "File size should be at least the size of the first chunk.");
                Assert.That(actualLength, Is.LessThan(fileSize), "File size should be less than the total size.");

            }, filePrefix: "test_cancel_part_");
        }
    }
}