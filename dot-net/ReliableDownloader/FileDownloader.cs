using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace ReliableDownloader;

internal sealed class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;
    private const int BufferSize = 81920;
    private const long DefaultChunkSize = 1 * 1024 * 1024;

    private const int DefaultMaxRetries = 5;
    private readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(1);
    private readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly int _maxRetries;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;

    // --- Fields for Progress Calculation ---
    private readonly Stopwatch _stopwatch = new();
    private long _startTimestampBytes = 0;
    // --- End Fields for Progress Calculation ---


    public FileDownloader(
        IWebSystemCalls webSystemCalls,
        int? maxRetries = null,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maxRetryDelay = null)
    {
        _webSystemCalls = webSystemCalls;
        _maxRetries = maxRetries ?? DefaultMaxRetries;
        _initialRetryDelay = initialRetryDelay ?? DefaultInitialRetryDelay;
        _maxRetryDelay = maxRetryDelay ?? DefaultMaxRetryDelay;
    }

    public async Task<bool> TryDownloadFile(
        string contentFileUrl,
        string localFilePath,
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        byte[]? expectedMd5Hash = null;
        long totalFileSize = 0;
        bool partialDownloadSupported = false;
        long totalBytesDownloaded = 0; // Keep track across method scope

        // Reset stopwatch for this download attempt
        _stopwatch.Reset();
        _startTimestampBytes = 0;


        try
        {
            Console.WriteLine("Attempting to get headers...");
            var headersResponse = await ExecuteWithRetryAsync(
                async (token) => await _webSystemCalls.GetHeadersAsync(contentFileUrl, token).ConfigureAwait(false),
                "GetHeaders",
                cancellationToken).ConfigureAwait(false);

            // Using statement ensures disposal even if exceptions occur later
            using (headersResponse)
            {
                if (headersResponse == null || !headersResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to get headers after retries. Status code: {headersResponse?.StatusCode}");
                    return false;
                }

                partialDownloadSupported = headersResponse.Headers.AcceptRanges.Contains("bytes");
                long? totalFileSizeNullable = headersResponse.Content.Headers.ContentLength;
                expectedMd5Hash = headersResponse.Content.Headers.ContentMD5;

                Console.WriteLine($"Partial download supported: {partialDownloadSupported}");
                Console.WriteLine($"Total file size: {totalFileSizeNullable?.ToString() ?? "Unknown"}");
                Console.WriteLine($"Expected MD5 Hash available: {expectedMd5Hash != null}");

                if (!totalFileSizeNullable.HasValue || totalFileSizeNullable.Value <= 0) // Allow 0 size files? No, treat as error.
                {
                    Console.WriteLine("Cannot determine file size or size is zero.");
                    return false;
                }
                totalFileSize = totalFileSizeNullable.Value;
            } // headersResponse disposed here

            // --- Resume Logic ---
            if (File.Exists(localFilePath))
            {
                try
                {
                    var existingFileInfo = new FileInfo(localFilePath);
                    totalBytesDownloaded = existingFileInfo.Length;
                    Console.WriteLine($"Existing file found with size: {totalBytesDownloaded} bytes.");

                    if (totalBytesDownloaded >= totalFileSize)
                    {
                        if (totalBytesDownloaded == totalFileSize)
                        {
                            Console.WriteLine("Existing file size matches total file size. Checking integrity...");
                            // Verify integrity even if file exists and seems complete
                            bool integrityOk = await VerifyIntegrityAsync(localFilePath, expectedMd5Hash, cancellationToken);
                            if (integrityOk)
                            {
                                Console.WriteLine("Integrity check passed for existing file. Assuming download complete.");
                                ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded); // Report final progress
                                return true;
                            }
                            else
                            {
                                Console.WriteLine("Existing file failed integrity check. Deleting and restarting.");
                                TryDeleteFile(localFilePath);
                                totalBytesDownloaded = 0;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Existing file size ({totalBytesDownloaded}) is larger than total size ({totalFileSize}). Deleting and restarting.");
                            TryDeleteFile(localFilePath);
                            totalBytesDownloaded = 0;
                        }
                    }
                    else if (partialDownloadSupported) // Only resume if server supports it
                    {
                        Console.WriteLine($"Resuming download from byte {totalBytesDownloaded}.");
                        // Set start bytes for speed calculation upon resume
                        _startTimestampBytes = totalBytesDownloaded;
                    }
                    else // File exists, smaller, but server doesn't support partial. Restart.
                    {
                        Console.WriteLine("Partial download not supported by server, but smaller local file exists. Deleting local file and starting full download.");
                        TryDeleteFile(localFilePath);
                        totalBytesDownloaded = 0;
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Error accessing existing file '{localFilePath}': {ex.Message}. Attempting to restart download.");
                    TryDeleteFile(localFilePath);
                    totalBytesDownloaded = 0;
                }
            }
            // --- End Resume Logic ---

            // Report initial progress (might be > 0 if resuming)
            ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded);

            // Start stopwatch just before download loop/call
            _stopwatch.Start();

            bool downloadAttemptSuccess = false;
            if (!partialDownloadSupported)
            {
                // Ensure we start clean if full download is needed but partial file existed
                if (totalBytesDownloaded > 0)
                {
                    Console.WriteLine("Starting full download, but partial file existed. Overwriting.");
                    _stopwatch.Restart(); // Restart timer for accurate speed
                    _startTimestampBytes = 0;
                    totalBytesDownloaded = 0;
                    ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded); // Reset progress report
                }
                downloadAttemptSuccess = await PerformFullDownloadAsync(contentFileUrl, localFilePath, totalFileSize, totalBytesDownloaded, onProgressChanged, cancellationToken);
            }
            else
            {
                // If not resuming (_startTimestampBytes == 0), set it now
                if (_startTimestampBytes == 0) _startTimestampBytes = totalBytesDownloaded;
                downloadAttemptSuccess = await PerformPartialDownloadAsync(contentFileUrl, localFilePath, totalFileSize, totalBytesDownloaded, onProgressChanged, cancellationToken);
            }

            _stopwatch.Stop(); // Stop after download attempt finishes or fails

            if (downloadAttemptSuccess)
            {
                Console.WriteLine("Download stream copy completed. Verifying integrity...");
                return await VerifyIntegrityAsync(localFilePath, expectedMd5Hash, cancellationToken);
            }
            else
            {
                Console.WriteLine("Download failed or was cancelled before completion.");
                // Don't delete partial file on general failure/cancellation (allow resume) unless specific error requires it (handled in download methods)
                return false;
            }
        }
        catch (OperationCanceledException) // Catch cancellation during header check/setup phase
        {
            Console.WriteLine("Operation cancelled during download setup (e.g., header check).");
            _stopwatch.Stop();
            throw;
        }
        catch (Exception ex) // Catch other unexpected errors during setup
        {
            Console.WriteLine($"An unexpected error occurred during download setup: {ex.Message}");
            _stopwatch.Stop();
            return false;
        }
    }

    // Modified to accept currentTotalBytesDownloaded
    private async Task<bool> PerformFullDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize, long currentTotalBytesDownloaded,
        Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine("Attempting full download...");
        HttpResponseMessage? contentResponse = null;
        try
        {
            contentResponse = await ExecuteWithRetryAsync(
                async (token) => await _webSystemCalls.DownloadContentAsync(contentFileUrl, token).ConfigureAwait(false),
                "DownloadContent (Full)",
                cancellationToken).ConfigureAwait(false);

            if (contentResponse == null || !contentResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to download content after retries. Status code: {contentResponse?.StatusCode}");
                return false;
            }

            if (contentResponse.Content.Headers.ContentLength.HasValue && contentResponse.Content.Headers.ContentLength.Value != totalFileSize)
            {
                Console.WriteLine($"Warning: Actual content length ({contentResponse.Content.Headers.ContentLength.Value}) differs from header content length ({totalFileSize}). Using actual.");
                totalFileSize = contentResponse.Content.Headers.ContentLength.Value;
            }

            Console.WriteLine($"Writing to file: {localFilePath}");
            using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            using var contentStream = await contentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[BufferSize];
            int bytesRead;
            // Use the bytes downloaded passed into the function
            long bytesDownloadedThisAttempt = 0;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                bytesDownloadedThisAttempt += bytesRead;
                // Report progress based on total downloaded so far
                ReportProgress(onProgressChanged, totalFileSize, currentTotalBytesDownloaded + bytesDownloadedThisAttempt);
            }
            await fileStream.FlushAsync(cancellationToken);
            Console.WriteLine("Full download stream copy complete.");
            return true;
        }
        catch (IOException ex) { Console.WriteLine($"File system error during full download: {ex.Message}"); TryDeleteFile(localFilePath); return false; }
        catch (OperationCanceledException) { Console.WriteLine("Cancellation occurred during full download stream copy."); TryDeleteFile(localFilePath); return false; }
        finally { contentResponse?.Dispose(); }
    }

    // Modified to track total bytes downloaded correctly
    private async Task<bool> PerformPartialDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize,
        long initialBytesDownloaded, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine("Attempting partial download...");
        long currentTotalBytesDownloaded = initialBytesDownloaded; // Use a local variable for tracking within this attempt
        FileStream? fileStream = null;

        try
        {
            fileStream = new FileStream(localFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            while (currentTotalBytesDownloaded < totalFileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long bytesRemaining = totalFileSize - currentTotalBytesDownloaded;
                long bytesToDownload = Math.Min(bytesRemaining, DefaultChunkSize);
                long rangeFrom = currentTotalBytesDownloaded;
                long rangeTo = rangeFrom + bytesToDownload - 1;

                // Check if range is valid before making the call
                if (rangeFrom > rangeTo || rangeFrom >= totalFileSize)
                {
                    Console.WriteLine($"Invalid range calculated ({rangeFrom}-{rangeTo}), total size {totalFileSize}. Assuming completion.");
                    break; // Exit loop if range seems invalid
                }

                Console.WriteLine($"Requesting bytes {rangeFrom}-{rangeTo}");
                HttpResponseMessage? partialResponse = null;
                try
                {
                    partialResponse = await ExecuteWithRetryAsync(
                        async (token) => await _webSystemCalls.DownloadPartialContentAsync(contentFileUrl, rangeFrom, rangeTo, token).ConfigureAwait(false),
                        $"DownloadPartialContent ({rangeFrom}-{rangeTo})",
                        cancellationToken).ConfigureAwait(false);

                    if (partialResponse == null || (partialResponse.StatusCode != HttpStatusCode.PartialContent && partialResponse.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable))
                    {
                        Console.WriteLine($"Failed to download chunk {rangeFrom}-{rangeTo} after retries. Status code: {partialResponse?.StatusCode}");
                        return false; // Chunk failed, abort attempt
                    }

                    if (partialResponse.StatusCode == HttpStatusCode.PartialContent)
                    {
                        fileStream.Seek(rangeFrom, SeekOrigin.Begin);
                        using var partialContentStream = await partialResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        var buffer = new byte[BufferSize];
                        int bytesRead;
                        long bytesReadThisChunk = 0;
                        while ((bytesRead = await partialContentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                            bytesReadThisChunk += bytesRead;
                            // Report progress based on total accumulated bytes
                            ReportProgress(onProgressChanged, totalFileSize, currentTotalBytesDownloaded + bytesReadThisChunk);
                        }
                        currentTotalBytesDownloaded += bytesReadThisChunk; // Update total after chunk is fully processed
                    }
                    else if (partialResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Console.WriteLine($"Server returned 416 Range Not Satisfiable for range {rangeFrom}-{rangeTo}. Assuming download already complete based on server response.");
                        // Update progress to 100% based on server feedback
                        ReportProgress(onProgressChanged, totalFileSize, totalFileSize);
                        currentTotalBytesDownloaded = totalFileSize; // Assume complete
                        break; // Exit loop
                    }
                }
                finally { partialResponse?.Dispose(); }

            } // End while loop

            if (fileStream != null) await fileStream.FlushAsync(cancellationToken);
            Console.WriteLine("Partial download stream copy loop finished.");
            // Final check based on whether we reached the total size
            return currentTotalBytesDownloaded >= totalFileSize;
        }
        catch (IOException ex) { Console.WriteLine($"File system error during partial download: {ex.Message}"); return false; }
        catch (OperationCanceledException) { Console.WriteLine("Cancellation occurred during partial download stream copy."); return false; /* Keep partial file */ }
        finally { fileStream?.Dispose(); }
    }

    private void ReportProgress(Action<FileProgress> onProgressChanged, long totalFileSize, long totalBytesDownloaded)
    {
        double? progressPercent = null;
        if (totalFileSize > 0)
        {
            progressPercent = Math.Max(0.0, Math.Min(100.0, (double)totalBytesDownloaded / totalFileSize * 100.0));
        }

        TimeSpan? estimatedRemaining = null;
        // Check stopwatch *before* accessing Elapsed
        if (_stopwatch.IsRunning && totalFileSize > 0 && totalBytesDownloaded > _startTimestampBytes)
        {
            double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;

            // Only calculate if some meaningful time has passed and bytes downloaded
            if (elapsedSeconds > 0.1)
            // -------------------------
            {
                long bytesSinceStart = totalBytesDownloaded - _startTimestampBytes;
                // Ensure bytesSinceStart is positive before division
                if (bytesSinceStart > 0)
                {
                    double bytesPerSecond = bytesSinceStart / elapsedSeconds;

                    // Avoid division by zero or tiny/negative speeds, report only for meaningful speed
                    if (bytesPerSecond > 1)
                    {
                        long remainingBytes = totalFileSize - totalBytesDownloaded;
                        if (remainingBytes > 0)
                        {
                            // Use try-catch for potential overflow if remainingBytes / bytesPerSecond is huge
                            try
                            {
                                estimatedRemaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);
                            }
                            catch (OverflowException)
                            {
                                // If time is huge, report TimeSpan.MaxValue or just null
                                estimatedRemaining = TimeSpan.MaxValue;
                            }
                        }
                        else
                        {
                            // If no bytes remaining, estimate is zero
                            estimatedRemaining = TimeSpan.Zero;
                        }
                    }
                }
            }
        }
        // Pass calculated value, default to Zero if null
        onProgressChanged?.Invoke(new FileProgress(totalFileSize, totalBytesDownloaded, progressPercent, estimatedRemaining ?? TimeSpan.Zero));
    }

    private async Task<bool> VerifyIntegrityAsync(string localFilePath, byte[]? expectedMd5Hash, CancellationToken cancellationToken)
    {
        if (expectedMd5Hash == null)
        {
            Console.WriteLine("Warning: No MD5 hash provided in headers. Skipping integrity check.");
            return true;
        }
        try
        {
            Console.WriteLine("Calculating MD5 hash for downloaded file...");
            byte[] calculatedHash;
            using (var md5 = MD5.Create())
            using (var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            {
                calculatedHash = await md5.ComputeHashAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            if (expectedMd5Hash.SequenceEqual(calculatedHash))
            {
                Console.WriteLine("Integrity check passed: MD5 hashes match.");
                return true;
            }
            else
            {
                Console.WriteLine("Integrity check failed: MD5 hashes do not match!");
                Console.WriteLine($"Expected:   {BitConverter.ToString(expectedMd5Hash).Replace("-", "")}");
                Console.WriteLine($"Calculated: {BitConverter.ToString(calculatedHash).Replace("-", "")}");
                TryDeleteFile(localFilePath);
                return false;
            }
        }
        catch (IOException ex) { Console.WriteLine($"Error reading file for integrity check: {ex.Message}"); return false; }
        catch (OperationCanceledException) { Console.WriteLine("Operation cancelled during integrity check."); return false; }
        catch (Exception ex) { Console.WriteLine($"Unexpected error during integrity check: {ex.Message}"); return false; }
    }

    private async Task<HttpResponseMessage?> ExecuteWithRetryAsync(
             Func<CancellationToken, Task<HttpResponseMessage>> action,
             string operationName,
             CancellationToken cancellationToken)
    {
        TimeSpan currentDelay = _initialRetryDelay;
        int maxAttempts = _maxRetries;

        for (int attempt = 0; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null; // Keep response scoped within loop iteration
            try
            {
                if (attempt > 0)
                {
                    Console.WriteLine($"Retrying {operationName} (attempt {attempt}/{maxAttempts}). Waiting {currentDelay.TotalSeconds}s...");
                    await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                    currentDelay = TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, _maxRetryDelay.TotalSeconds));
                }

                response = await action(cancellationToken).ConfigureAwait(false);

                // Success or specific non-retriable codes (like 416) - RETURN without disposing
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Let the caller handle disposal of the successful/handled response
                    return response;
                }

                // Decide if status code is worth retrying
                bool shouldRetryStatusCode = response.StatusCode >= HttpStatusCode.InternalServerError ||
                                             response.StatusCode == HttpStatusCode.RequestTimeout || // 408
                                             response.StatusCode == HttpStatusCode.TooManyRequests; // 429

                if (!shouldRetryStatusCode)
                {
                    Console.WriteLine($"{operationName} failed on attempt {attempt} with non-retriable status code {response.StatusCode}.");
                    // Let the caller handle disposal of the non-retried failed response
                    return response;
                }

                Console.WriteLine($"{operationName} failed on attempt {attempt} with retriable status code {response.StatusCode}.");

                // Dispose *only* if we are going to retry (i.e., not the last attempt)
                if (attempt < maxAttempts)
                {
                    response.Dispose();
                    // Set response to null after disposal to avoid potential issues if catch block runs later
                    response = null;
                }
                else
                {
                    // Return the failed response after max retries - let caller dispose
                    return response;
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"{operationName} failed on attempt {attempt} with network error: {ex.Message}");
                response?.Dispose(); // Dispose if exception caught
                if (attempt >= maxAttempts) throw; // Rethrow after max retries
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Console.WriteLine($"{operationName} failed on attempt {attempt} due to timeout.");
                response?.Dispose(); // Dispose if exception caught
                if (attempt >= maxAttempts) throw; // Rethrow after max retries
            }
            // --- NO finally block here disposing the response ---
        }
        // This path should ideally not be reached if MaxRetries >= 0
        Console.WriteLine($"Warning: {operationName} ExecuteWithRetryAsync loop completed without returning or throwing.");
        return null;
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Console.WriteLine($"Attempting to delete file: {filePath}");
                File.Delete(filePath);
            }
        }
        catch (IOException ex) { Console.WriteLine($"Warning: Could not delete file '{filePath}': {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Console.WriteLine($"Warning: No permission to delete file '{filePath}': {ex.Message}"); }
    }
}