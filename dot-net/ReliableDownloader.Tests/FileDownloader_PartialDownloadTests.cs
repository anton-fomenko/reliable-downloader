﻿using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy; // Needed for CollectionAssert
using System.Net;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_PartialDownloadTests
    {
        private FileDownloaderTestHelper.TestContextData _context = null!;

        [SetUp]
        public void Setup()
        {
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
        }

        [TearDown]
        public void Teardown()
        {
            FileDownloaderTestHelper.TeardownTestEnvironment(_context);
        }

        [Test]
        public async Task TryDownloadFile_ShouldAttemptPartialDownload_WhenPartialSupportedAndHeadersPresent()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://partial-support.com/file.msi";
                int fileSize = 1024;
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, 0, fileSize - 1)));

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, fileSize - 1, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldPerformPartialDownload_InMultipleChunks_WhenSupported()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://multi-partial-support.com/file.msi";
                int fileSize = (int)(FileDownloaderTestHelper.DefaultTestChunkSize * 2.5); // Requires 3 chunks
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                long currentPos = 0;
                int expectedCalls = 0;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, FileDownloaderTestHelper.DefaultTestChunkSize);
                    long rangeFrom = currentPos;
                    long rangeTo = rangeFrom + bytesToDownload - 1;
                    long currentRangeFrom = rangeFrom; // Capture loop variables
                    long currentRangeTo = rangeTo;
                    _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, currentRangeFrom, currentRangeTo, _context.Cts.Token))
                                 .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, currentRangeFrom, currentRangeTo)));
                    currentPos += bytesToDownload;
                    expectedCalls++;
                }

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _context.Cts.Token), Times.Exactly(expectedCalls));

                // Explicitly verify each range call
                currentPos = 0;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, FileDownloaderTestHelper.DefaultTestChunkSize);
                    _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _context.Cts.Token), Times.Once);
                    currentPos += bytesToDownload;
                }

                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldResumePartialDownload_WhenFileExists()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://resume-partial.com/file.msi";
                int fileSize = (int)(FileDownloaderTestHelper.DefaultTestChunkSize * 1.5); // Requires 2 chunks total
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);
                int existingSize = (int)(FileDownloaderTestHelper.DefaultTestChunkSize / 2); // Partial file exists
                var existingData = testData.Take(existingSize).ToArray();
                await File.WriteAllBytesAsync(uniqueTestFilePath, existingData);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, true)));

                // Mock the partial download calls for the REMAINING chunks/bytes
                long currentPos = existingSize;
                int expectedCalls = 0;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, FileDownloaderTestHelper.DefaultTestChunkSize);
                    long rangeFrom = currentPos;
                    long rangeTo = rangeFrom + bytesToDownload - 1;
                    long currentRangeFrom = rangeFrom; // Capture loop variables
                    long currentRangeTo = rangeTo;
                    _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, currentRangeFrom, currentRangeTo, _context.Cts.Token))
                                 .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(testData, currentRangeFrom, currentRangeTo)));
                    currentPos += bytesToDownload;
                    expectedCalls++;
                }

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, 0, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never); // Should NOT start from 0
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.IsAny<long>(), It.IsAny<long>(), _context.Cts.Token), Times.Exactly(expectedCalls)); // Verify remaining calls

                // Explicitly verify the ranges that *should* have been called
                currentPos = existingSize;
                while (currentPos < fileSize)
                {
                    long bytesToDownload = Math.Min(fileSize - currentPos, FileDownloaderTestHelper.DefaultTestChunkSize);
                    _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, currentPos, currentPos + bytesToDownload - 1, _context.Cts.Token), Times.Once);
                    currentPos += bytesToDownload;
                }

                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenPartialDownloadChunkFailsAfterRetries()
        {
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

                // Mock the second chunk to fail consistently (using status code)
                long rangeFrom2 = FileDownloaderTestHelper.DefaultTestChunkSize, rangeTo2 = fileSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.False);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom1, rangeTo1, _context.Cts.Token), Times.Once);
                // Verify second chunk called initial + max retries
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
                Assert.That(File.Exists(uniqueTestFilePath), Is.True); // Partial file should still exist
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(FileDownloaderTestHelper.DefaultTestChunkSize)); // Size is only the first chunk
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenPartialChunkFailsOnceThenSucceeds()
        {
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
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom2, rangeTo2, _context.Cts.Token), Times.Exactly(2)); // Called twice
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldRestartDownload_WhenExistingFileIsLargerThanTotalSize_AndPartialSupported()
        {
            // Verifies restart logic even if partial is supported, due to invalid local file size
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://local-larger-partial-ok.com/file.msi";
                int serverFileSize = 1000;
                int localFileSize = 1500; // Existing file is larger
                var serverData = FileDownloaderTestHelper.GenerateTestData(serverFileSize);
                var localData = FileDownloaderTestHelper.GenerateTestData(localFileSize);
                await File.WriteAllBytesAsync(uniqueTestFilePath, localData);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(serverFileSize, true)));

                // Mock the partial download for the *entire file* which should happen after the restart
                long rangeFrom = 0, rangeTo = serverFileSize - 1;
                _context.MockWebCalls.Setup(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreatePartialContentResponse(serverData, rangeFrom, rangeTo)));

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, rangeFrom, rangeTo, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(url, It.Is<long>(l => l != 0), It.IsAny<long>(), _context.Cts.Token), Times.Never);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(serverData, await File.ReadAllBytesAsync(uniqueTestFilePath));
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(serverFileSize));
            });
        }
    }
}