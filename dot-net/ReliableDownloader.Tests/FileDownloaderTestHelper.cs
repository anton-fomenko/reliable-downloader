using Moq;
using NUnit.Framework; // For TestContext
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography; // Required for MD5

namespace ReliableDownloader.Tests
{
    // Internal static class within the test project
    internal static class FileDownloaderTestHelper
    {
        // --- Constants ---
        // Keep constants accessible if needed by multiple tests/helpers
        public const long DefaultChunkSize = 1 * 1024 * 1024; // 1MB chunk size
        public const int TestMaxRetries = 1; // Use minimal retries for faster tests
        public static readonly TimeSpan TestInitialDelay = TimeSpan.Zero;
        public static readonly TimeSpan TestMaxDelay = TimeSpan.FromMilliseconds(1);
        public const string DefaultTestFilePathPrefix = "test_dl_";

        // Context class to hold shared setup items
        public class TestContextData
        {
            public Mock<IWebSystemCalls> MockWebCalls { get; init; } = null!;
            public FileDownloader Sut { get; init; } = null!;
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

            var sut = new FileDownloader(
                mockWebCalls.Object,
                maxRetries: TestMaxRetries,
                initialRetryDelay: TestInitialDelay,
                maxRetryDelay: TestMaxDelay
            );

            return new TestContextData
            {
                MockWebCalls = mockWebCalls,
                Sut = sut,
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
            foreach (var filePath in context.FilesToCleanup)
            {
                CleanUpFile(filePath); // Call the static helper
            }
        }


        // --- Static Helper Methods ---

        public static async Task ExecuteWithUniqueFileAsync(TestContextData context, Func<string, Task> testAction, string filePrefix = DefaultTestFilePathPrefix, string extension = ".msi")
        {
            var uniqueFilePath = $"{filePrefix}{Guid.NewGuid()}{extension}";
            context.FilesToCleanup.Add(uniqueFilePath); // Track file for cleanup
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

        public static void CleanUpFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                    TestContext.WriteLine($"Cleaned up test file: {filePath}");
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine($"!!! WARNING: Failed to clean up test file '{filePath}': {ex.Message}");
                }
            }
        }

        // Note: HandleProgress doesn't need context if it just adds to the list
        public static void HandleProgress(TestContextData context, FileProgress progress)
        {
            context.ProgressUpdates.Add(progress);
            // TestContext.WriteLine($"Progress: {progress}");
        }


        public static HttpResponseMessage CreateHeadersResponse(long contentLength, bool supportPartial, byte[]? md5 = null)
        {
            // Same implementation as before
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (supportPartial) response.Headers.AcceptRanges.Add("bytes");
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentLength = contentLength;
            if (md5 != null) response.Content.Headers.ContentMD5 = md5;
            return response;
        }

        public static HttpResponseMessage CreatePartialContentResponse(byte[] fullData, long rangeFrom, long rangeTo)
        {
            // Same implementation as before
            if (rangeFrom >= fullData.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            rangeTo = Math.Min(rangeTo, fullData.Length - 1);
            if (rangeTo < rangeFrom) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            var partialData = fullData.Skip((int)rangeFrom).Take((int)(rangeTo - rangeFrom + 1)).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
            response.Content = new ByteArrayContent(partialData);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(rangeFrom, rangeTo, fullData.Length);
            response.Content.Headers.ContentLength = partialData.Length;
            return response;
        }

        public static byte[] GenerateTestData(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray();

        public static byte[] ComputeMd5(byte[] data)
        {
            using (var md5 = MD5.Create()) { return md5.ComputeHash(data); }
        }

        // Move SlowStream here as a static nested class if used across multiple test files
        public class SlowStream : MemoryStream
        {
            // Implementation remains the same as before, potentially making _testCancellationToken public if needed outside
            private readonly int _delayMs;
            private readonly CancellationToken _testCancellationToken;

            public SlowStream(byte[] buffer, int delayMs, CancellationToken testCancellationToken) : base(buffer)
            {
                _delayMs = delayMs;
                _testCancellationToken = testCancellationToken;
            }
            // ... ReadAsync overrides ...
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) // cancellationToken passed by the caller (e.g., Stream.CopyToAsync)
            {
                // Link the test's token with the token passed into ReadAsync
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_testCancellationToken, cancellationToken);
                try
                {
                    if (_delayMs > 0) await Task.Delay(_delayMs, linkedCts.Token);
                    linkedCts.Token.ThrowIfCancellationRequested(); // Check if cancellation was requested (either from test or caller)
                    return await base.ReadAsync(buffer, offset, count, linkedCts.Token);
                }
                catch (OperationCanceledException) when (_testCancellationToken.IsCancellationRequested) { throw; } // Test initiated cancellation
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // Caller initiated cancellation
                // Rethrow any other exception
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // Link the test's token with the token passed into ReadAsync
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_testCancellationToken, cancellationToken);
                try
                {
                    if (_delayMs > 0) await Task.Delay(_delayMs, linkedCts.Token);
                    linkedCts.Token.ThrowIfCancellationRequested(); // Check if cancellation was requested
                    return await base.ReadAsync(buffer, linkedCts.Token);
                }
                catch (OperationCanceledException) when (_testCancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                // Rethrow any other exception
            }
        }
    }
}