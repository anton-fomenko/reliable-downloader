using Moq;
using NUnit.Framework; // For TestContext
using System.Net; // Required for HttpStatusCode, HttpResponseMessage
using System.Net.Http.Headers; // Required for ContentRangeHeaderValue
using System.Security.Cryptography; // Required for MD5

namespace ReliableDownloader.Tests
{
    // Internal static class within the test project
    internal static class FileDownloaderTestHelper
    {
        // --- Constants ---
        public const long DefaultTestChunkSize = 1 * 1024 * 1024; // 1MB chunk size (Keep separate from main code options if needed)
        public const int DefaultTestBufferSize = 81920; // Keep consistent or configure as needed
        public const int TestMaxRetries = 1; // Use minimal retries for faster tests
        public static readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        public static readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);
        public const string DefaultTestFilePathPrefix = "test_dl_";

        // Context class to hold shared setup items
        public class TestContextData
        {
            public Mock<IWebSystemCalls> MockWebCalls { get; init; } = null!;
            public FileDownloader Sut { get; init; } = null!;
            public FileDownloaderOptions DownloaderOptions { get; init; } = null!; // Keep options if needed for assertions
            public List<FileProgress> ProgressUpdates { get; init; } = null!;
            public CancellationTokenSource Cts { get; init; } = null!;
            public List<string> FilesToCleanup { get; init; } = null!;
        }

        /// <summary>
        /// Performs common setup for FileDownloader tests.
        /// </summary>
        public static TestContextData SetupTestEnvironment()
        {
            var mockWebCalls = new Mock<IWebSystemCalls>();
            var cts = new CancellationTokenSource();
            var progressUpdates = new List<FileProgress>();
            var filesToCleanup = new List<string>();

            // Create specific options for testing
            var testOptions = new FileDownloaderOptions
            {
                MaxRetries = TestMaxRetries,
                InitialRetryDelay = TestInitialDelay,
                MaxRetryDelay = TestMaxDelay,
                ChunkSize = DefaultTestChunkSize, // Use test-specific chunk size
                BufferSize = DefaultTestBufferSize // Use test-specific buffer size
            };

            // Instantiate FileDownloader using the options object
            var sut = new FileDownloader(
                mockWebCalls.Object,
                testOptions // Pass the options object
            );

            return new TestContextData
            {
                MockWebCalls = mockWebCalls,
                Sut = sut,
                DownloaderOptions = testOptions, // Store the options used
                ProgressUpdates = progressUpdates,
                Cts = cts,
                FilesToCleanup = filesToCleanup
            };
        }

        /// <summary>
        /// Performs common teardown actions.
        /// </summary>
        public static void TeardownTestEnvironment(TestContextData context)
        {
            context.Cts?.Dispose();
            // Use a copy of the list in case CleanUpFile modifies it (though it shouldn't)
            var files = new List<string>(context.FilesToCleanup);
            foreach (var filePath in files)
            {
                CleanUpFile(filePath);
            }
            context.FilesToCleanup.Clear(); // Clear original list
        }


        // --- Static Helper Methods ---

        public static async Task ExecuteWithUniqueFileAsync(TestContextData context, Func<string, Task> testAction, string filePrefix = DefaultTestFilePathPrefix, string extension = ".msi")
        {
            var uniqueFilePath = Path.Combine(Path.GetTempPath(), $"{filePrefix}{Guid.NewGuid()}{extension}"); // Use Temp path for safety
            context.FilesToCleanup.Add(uniqueFilePath); // Track file for cleanup
            CleanUpFile(uniqueFilePath); // Ensure clean state before test
            try
            {
                await testAction(uniqueFilePath);
            }
            finally
            {
                // Teardown handles the actual cleanup using the FilesToCleanup list
            }
        }

        public static void CleanUpFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    // Use TestContext for output within NUnit tests
                    TestContext.WriteLine($"Cleaned up test file: {filePath}");
                }
                catch (IOException ex) // More specific catch
                {
                    TestContext.WriteLine($"!!! WARNING: IO Error cleaning up test file '{filePath}': {ex.Message}");
                }
                catch (UnauthorizedAccessException ex) // More specific catch
                {
                    TestContext.WriteLine($"!!! WARNING: Access Denied cleaning up test file '{filePath}': {ex.Message}");
                }
                catch (Exception ex) // General catch for other issues
                {
                    TestContext.WriteLine($"!!! WARNING: Failed to clean up test file '{filePath}': {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        // Handle progress callback
        public static void HandleProgress(TestContextData context, FileProgress progress)
        {
            context.ProgressUpdates.Add(progress);
            // Uncomment for verbose progress logging during tests:
            // TestContext.WriteLine($"Progress: Bytes={progress.TotalBytesDownloaded}, Percent={progress.ProgressPercent:F1}%, EstRemaining={progress.EstimatedRemaining?.ToString() ?? "N/A"}");
        }


        public static HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (supportPartial)
            {
                response.Headers.AcceptRanges.Add("bytes");
            }
            // Create empty content for HEAD response, but set headers on it
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentLength = contentLength;
            if (md5 != null)
            {
                response.Content.Headers.ContentMD5 = md5;
            }
            return response;
        }

        public static HttpResponseMessage CreatePartialContentResponse(byte[] fullData, long rangeFrom, long rangeTo)
        {
            if (rangeFrom < 0 || rangeFrom >= fullData.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }
            // Adjust rangeTo to be within bounds
            rangeTo = Math.Min(rangeTo, fullData.Length - 1);

            if (rangeTo < rangeFrom)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            int start = (int)rangeFrom;
            int count = (int)(rangeTo - rangeFrom + 1);

            // Use ArraySegment or Span for potentially better performance if data is large
            var partialData = new byte[count];
            Buffer.BlockCopy(fullData, start, partialData, 0, count);
            //var partialData = fullData.Skip(start).Take(count).ToArray(); // LINQ alternative

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(partialData)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
            response.Content.Headers.ContentLength = partialData.Length; // Length of the actual partial data
            return response;
        }

        public static byte[] GenerateTestData(int size)
        {
            var data = new byte[size];
            // Simple pattern, ensure it doesn't repeat too quickly if needed
            for (int i = 0; i < size; i++)
            {
                data[i] = (byte)(i % 251); // Use a prime number for slightly better distribution
            }
            return data;
            // LINQ alternative (can be slower for large sizes):
            // return Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        }

        public static byte[] ComputeMd5(byte[] data)
        {
            // MD5.Create() is obsolete, use Create("MD5") or prefer SHA256 if possible
            using (var md5 = MD5.Create())
            {
                if (md5 == null) throw new InvalidOperationException("Could not create MD5 instance.");
                return md5.ComputeHash(data);
            }
        }

        // --- SlowStream ---
        // Nested class to simulate delays during streaming
        public class SlowStream : MemoryStream
        {
            private readonly int _delayMs;
            private readonly CancellationToken _testCancellationToken; // Token controlled by the test itself

            public SlowStream(byte[] buffer, int delayMs, CancellationToken testCancellationToken) : base(buffer)
            {
                _delayMs = delayMs;
                _testCancellationToken = testCancellationToken;
            }

            // Override ReadAsync(byte[], ...)
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) // This token comes from the SUT (e.g., Stream.CopyToAsync)
            {
                // Link the test's token with the token passed into ReadAsync from the caller
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_testCancellationToken, cancellationToken);
                try
                {
                    if (_delayMs > 0)
                    {
                        await Task.Delay(_delayMs, linkedCts.Token); // Wait respects combined cancellation
                    }

                    // Throw if *either* token requested cancellation *before* reading
                    linkedCts.Token.ThrowIfCancellationRequested();

                    // Perform the actual read, passing the combined token
                    return await base.ReadAsync(buffer, offset, count, linkedCts.Token);
                }
                catch (OperationCanceledException ex) when (ex.CancellationToken == linkedCts.Token)
                {
                    // Determine which token caused the cancellation if needed for specific assertions
                    if (_testCancellationToken.IsCancellationRequested)
                    {
                        // TestContext.WriteLine("SlowStream cancelled by TEST token.");
                        // Re-throw specific exception maybe? Or just let it propagate.
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        // TestContext.WriteLine("SlowStream cancelled by CALLER token.");
                    }
                    throw; // Re-throw the OperationCanceledException
                }
                // Catch other exceptions if necessary
            }

            // Override ReadAsync(Memory<byte>, ...)
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) // This token comes from the SUT
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
                    // Optional logging to distinguish cancellation source
                    // if (_testCancellationToken.IsCancellationRequested) { ... }
                    // else if (cancellationToken.IsCancellationRequested) { ... }
                    throw;
                }
            }
        }
    }
}