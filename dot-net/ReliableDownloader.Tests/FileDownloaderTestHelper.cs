using Moq;
using NUnit.Framework;
using System.Net;
using System.Net.Http.Headers; 
using System.Security.Cryptography; 

namespace ReliableDownloader.Tests
{
    internal static class FileDownloaderTestHelper
    {
        // --- Constants ---
        public const long DefaultTestChunkSize = 1 * 1024 * 1024; // 1MB chunk size
        public const int DefaultTestBufferSize = 81920;
        public const int TestMaxRetries = 1; // Use minimal retries for faster tests
        public static readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        public static readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);
        public const string DefaultTestFilePathPrefix = "test_dl_";

        /// <summary>
        /// Holds shared objects and configuration commonly used across downloader tests.
        /// </summary>
        public class TestContextData
        {
            public Mock<IWebSystemCalls> MockWebCalls { get; init; } = null!;
            public FileDownloader Sut { get; init; } = null!;
            public FileDownloaderOptions DownloaderOptions { get; init; } = null!;
            public List<FileProgress> ProgressUpdates { get; init; } = null!;
            public CancellationTokenSource Cts { get; init; } = null!;
            public List<string> FilesToCleanup { get; init; } = null!;
        }

        /// <summary>
        /// Performs common setup for FileDownloader tests, creating mocks, options, and the SUT.
        /// </summary>
        public static TestContextData SetupTestEnvironment()
        {
            var mockWebCalls = new Mock<IWebSystemCalls>();
            var cts = new CancellationTokenSource();
            var progressUpdates = new List<FileProgress>();
            var filesToCleanup = new List<string>();

            var testOptions = new FileDownloaderOptions
            {
                MaxRetries = TestMaxRetries,
                InitialRetryDelay = TestInitialDelay,
                MaxRetryDelay = TestMaxDelay,
                ChunkSize = DefaultTestChunkSize,
                BufferSize = DefaultTestBufferSize
            };

            var sut = new FileDownloader(
                mockWebCalls.Object,
                testOptions
            );

            return new TestContextData
            {
                MockWebCalls = mockWebCalls,
                Sut = sut,
                DownloaderOptions = testOptions,
                ProgressUpdates = progressUpdates,
                Cts = cts,
                FilesToCleanup = filesToCleanup
            };
        }

        /// <summary>
        /// Performs common teardown actions, disposing resources and cleaning up generated files.
        /// </summary>
        public static void TeardownTestEnvironment(TestContextData context)
        {
            context.Cts?.Dispose();
            // Create a copy of the list to avoid issues if CleanUpFile were to modify the collection while iterating.
            var files = new List<string>(context.FilesToCleanup);
            foreach (var filePath in files)
            {
                CleanUpFile(filePath);
            }
            context.FilesToCleanup.Clear();
        }


        // --- Static Helper Methods ---

        /// <summary>
        /// Executes a test action within a context that ensures a unique temporary file is created and cleaned up.
        /// </summary>
        /// <param name="context">The current test context.</param>
        /// <param name="testAction">The async test action to execute, taking the unique file path as input.</param>
        /// <param name="filePrefix">Prefix for the temporary file name.</param>
        /// <param name="extension">Extension for the temporary file name.</param>
        public static async Task ExecuteWithUniqueFileAsync(TestContextData context, Func<string, Task> testAction, string filePrefix = DefaultTestFilePathPrefix, string extension = ".msi")
        {
            var uniqueFilePath = Path.Combine(Path.GetTempPath(), $"{filePrefix}{Guid.NewGuid()}{extension}"); // Use Temp path for safety
            context.FilesToCleanup.Add(uniqueFilePath);
            CleanUpFile(uniqueFilePath); // Ensure clean state before test
            try
            {
                await testAction(uniqueFilePath);
            }
            finally
            {
                // TeardownTestEnvironment handles the actual cleanup
            }
        }

        /// <summary>
        /// Safely attempts to delete a file, logging warnings if deletion fails.
        /// </summary>
        /// <param name="filePath">The path to the file to delete.</param>
        public static void CleanUpFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    TestContext.WriteLine($"Cleaned up test file: {filePath}");
                }
                catch (IOException ex)
                {
                    TestContext.WriteLine($"!!! WARNING: IO Error cleaning up test file '{filePath}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    TestContext.WriteLine($"!!! WARNING: Access Denied cleaning up test file '{filePath}': {ex.Message}");
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine($"!!! WARNING: Failed to clean up test file '{filePath}': {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Basic progress handler that adds updates to the context's list.
        /// </summary>
        public static void HandleProgress(TestContextData context, FileProgress progress)
        {
            context.ProgressUpdates.Add(progress);
        }

        /// <summary>
        /// Creates a mock HttpResponseMessage for a HEAD request.
        /// </summary>
        /// <param name="contentLength">The value for the Content-Length header.</param>
        /// <param name="supportPartial">Whether the server supports partial requests (Accept-Ranges: bytes).</param>
        /// <param name="md5">Optional Content-MD5 header value.</param>
        /// <returns>An HttpResponseMessage configured for a HEAD response.</returns>
        public static HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (supportPartial)
            {
                response.Headers.AcceptRanges.Add("bytes");
            }
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentLength = contentLength;
            if (md5 != null)
            {
                response.Content.Headers.ContentMD5 = md5;
            }
            return response;
        }

        /// <summary>
        /// Creates a mock HttpResponseMessage for a partial content (206) response.
        /// </summary>
        /// <param name="fullData">The complete byte array representing the full file.</param>
        /// <param name="rangeFrom">The starting byte index of the requested range.</param>
        /// <param name="rangeTo">The ending byte index of the requested range.</param>
        /// <returns>An HttpResponseMessage configured for a partial content response.</returns>
        public static HttpResponseMessage CreatePartialContentResponse(byte[] fullData, long rangeFrom, long rangeTo)
        {
            if (rangeFrom < 0 || rangeFrom >= fullData.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }
            rangeTo = Math.Min(rangeTo, fullData.Length - 1);

            if (rangeTo < rangeFrom)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            int start = (int)rangeFrom;
            int count = (int)(rangeTo - rangeFrom + 1);

            var partialData = new byte[count];
            Buffer.BlockCopy(fullData, start, partialData, 0, count);

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(partialData)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
            response.Content.Headers.ContentLength = partialData.Length;
            return response;
        }

        /// <summary>
        /// Generates a predictable byte array for test data.
        /// </summary>
        /// <param name="size">The desired size of the byte array.</param>
        /// <returns>A byte array with predictable content.</returns>
        public static byte[] GenerateTestData(int size)
        {
            var data = new byte[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = (byte)(i % 251); // Use a prime number for slightly better distribution
            }
            return data;
        }

        /// <summary>
        /// Computes the MD5 hash for the given byte array.
        /// </summary>
        /// <param name="data">The input data.</param>
        /// <returns>The computed MD5 hash.</returns>
        public static byte[] ComputeMd5(byte[] data)
        {
            // MD5.Create() is obsolete, use Create("MD5") or prefer SHA256 if possible
            using (var md5 = MD5.Create("MD5")) // Specify "MD5" explicitly
            {
                if (md5 == null) throw new InvalidOperationException("Could not create MD5 instance.");
                return md5.ComputeHash(data);
            }
        }

        // --- SlowStream ---
        /// <summary>
        /// Nested class to simulate delays during streaming for cancellation tests.
        /// </summary>
        public class SlowStream : MemoryStream
        {
            private readonly int _delayMs;
            private readonly CancellationToken _testCancellationToken; // Token controlled by the test itself

            public SlowStream(byte[] buffer, int delayMs, CancellationToken testCancellationToken) : base(buffer)
            {
                _delayMs = delayMs;
                _testCancellationToken = testCancellationToken;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) // cancellationToken comes from the SUT (e.g., Stream.CopyToAsync)
            {
                // Link the test's token with the token passed into ReadAsync
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_testCancellationToken, cancellationToken);
                try
                {
                    if (_delayMs > 0)
                    {
                        await Task.Delay(_delayMs, linkedCts.Token); // Wait respects combined cancellation
                    }

                    linkedCts.Token.ThrowIfCancellationRequested();

                    return await base.ReadAsync(buffer, offset, count, linkedCts.Token);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == linkedCts.Token)
                {
                    throw;
                }
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) // cancellationToken comes from the SUT
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_testCancellationToken, cancellationToken);
                try
                {
                    if (_delayMs > 0)
                    {
                        await Task.Delay(_delayMs, linkedCts.Token);
                    }
                    linkedCts.Token.ThrowIfCancellationRequested();
                    return await base.ReadAsync(buffer, linkedCts.Token);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == linkedCts.Token)
                {
                    throw;
                }
            }
        }
    }
}