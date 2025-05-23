﻿using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Net;

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_FullDownloadTests
    {
        // Store the context returned by the helper
        private FileDownloaderTestHelper.TestContextData _context = null!;

        [SetUp]
        public void Setup()
        {
            // Get the common setup from the helper
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
        }

        [TearDown]
        public void Teardown()
        {
            FileDownloaderTestHelper.TeardownTestEnvironment(_context);
        }

        [Test]
        public async Task TryDownloadFile_ShouldPerformFullDownload_WhenPartialNotSupported()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://no-partial-support.com/file.msi";
                int fileSize = 100;
                var fileBytes = FileDownloaderTestHelper.GenerateTestData(fileSize);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fileBytes) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.GetHeadersAsync(url, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Once);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(fileBytes, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadFails()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://full-download-fails.com/file.msi";
                long fileSize = 1024;

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

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
        public async Task TryDownloadFile_ShouldReturnFalse_WhenFullDownloadThrowsNetworkError()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://full-download-network-error.com/file.msi";
                long fileSize = 1024;

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .ThrowsAsync(new HttpRequestException("Network error during download"));

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.False);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Exactly(FileDownloaderTestHelper.TestMaxRetries + 1));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldRestartFullDownload_WhenExistingFileIsLargerAndPartialNotSupported()
        {
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://local-larger-no-partial.com/file.msi";
                int serverFileSize = 1000;
                int localFileSize = 1500;
                var serverData = FileDownloaderTestHelper.GenerateTestData(serverFileSize);
                var localData = FileDownloaderTestHelper.GenerateTestData(localFileSize);
                await File.WriteAllBytesAsync(uniqueTestFilePath, localData);

                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(serverFileSize, false)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(serverData) };
                                 contentResponse.Content.Headers.ContentLength = serverFileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                _context.MockWebCalls.Verify(w => w.DownloadPartialContentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
                _context.MockWebCalls.Verify(w => w.DownloadContentAsync(url, _context.Cts.Token), Times.Once);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(serverData, await File.ReadAllBytesAsync(uniqueTestFilePath));
                Assert.That(new FileInfo(uniqueTestFilePath).Length, Is.EqualTo(serverFileSize));
            });
        }
    }
}