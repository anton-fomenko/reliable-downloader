using Moq;
using NUnit.Framework;
using NUnit.Framework.Legacy; // Needed for CollectionAssert
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography; // Required for MD5 generation in tests
// Make sure your helper class namespace is accessible
// using static ReliableDownloader.Tests.FileDownloaderTestHelper; // Optional

namespace ReliableDownloader.Tests
{
    [TestFixture]
    public class FileDownloader_IntegrityTests // No inheritance needed
    {
        // Store the context returned by the helper
        private FileDownloaderTestHelper.TestContextData _context = null!;

        [SetUp]
        public void Setup()
        {
            // Get the common setup from the helper
            _context = FileDownloaderTestHelper.SetupTestEnvironment();
            // No specific additional setup needed for these tests beyond cleanup
        }

        [TearDown]
        public void Teardown()
        {
            // Call the common teardown helper
            FileDownloaderTestHelper.TeardownTestEnvironment(_context);
        }

        // =========================================
        // MD5 Integrity Tests
        // =========================================

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenMd5Matches()
        {
            // Use static helper, passing context
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://md5-match.com/file.msi";
                int fileSize = 500;
                // Use static helpers
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);
                byte[] correctMd5 = FileDownloaderTestHelper.ComputeMd5(testData);

                // Use context mock and static helper
                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false, correctMd5)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                // Use context SUT, helper for progress, context CTS
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True);
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldFailAndCleanup_WhenMd5Mismatch()
        {
            // Use static helper, passing context
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://md5-mismatch.com/file.msi";
                int fileSize = 500;
                // Use static helpers
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);
                byte[] correctMd5 = FileDownloaderTestHelper.ComputeMd5(testData);
                byte[] incorrectMd5 = correctMd5.Select(b => (byte)(~b)).ToArray(); // Create incorrect hash

                // Use context mock and static helper
                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false, incorrectMd5)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });


                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.False); // Should fail due to mismatch
                Assert.That(File.Exists(uniqueTestFilePath), Is.False, "File should be deleted after MD5 mismatch.");
            });
        }

        [Test]
        public async Task TryDownloadFile_ShouldSucceed_WhenMd5HeaderMissing()
        {
            // Use static helper, passing context
            await FileDownloaderTestHelper.ExecuteWithUniqueFileAsync(_context, async uniqueTestFilePath =>
            {
                // Arrange
                var url = "http://md5-missing.com/file.msi";
                int fileSize = 500;
                // Use static helper
                var testData = FileDownloaderTestHelper.GenerateTestData(fileSize);

                // Use context mock and static helper (passing null for md5)
                _context.MockWebCalls.Setup(w => w.GetHeadersAsync(url, _context.Cts.Token))
                             .Returns(() => Task.FromResult(FileDownloaderTestHelper.CreateHeadersResponse(fileSize, false, md5: null)));

                _context.MockWebCalls.Setup(w => w.DownloadContentAsync(url, _context.Cts.Token))
                             .Returns(() => {
                                 var contentResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(testData) };
                                 contentResponse.Content.Headers.ContentLength = fileSize;
                                 return Task.FromResult(contentResponse);
                             });

                // Act
                var result = await _context.Sut.TryDownloadFile(url, uniqueTestFilePath, prog => FileDownloaderTestHelper.HandleProgress(_context, prog), _context.Cts.Token);

                // Assert
                Assert.That(result, Is.True); // Should succeed as check is skipped
                Assert.That(File.Exists(uniqueTestFilePath), Is.True);
                CollectionAssert.AreEqual(testData, await File.ReadAllBytesAsync(uniqueTestFilePath));
            });
        }
    }
}