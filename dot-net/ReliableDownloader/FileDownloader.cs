using System.Net;
using System.Security.Cryptography;

namespace ReliableDownloader;

internal sealed class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;
    private const int BufferSize = 81920; // 80 KB buffer for copying streams
    private const long DefaultChunkSize = 1 * 1024 * 1024; // 1MB chunks for partial download

    // --- Original constants ---
    private const int DefaultMaxRetries = 5;
    private readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(1);
    private readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(30);

    // --- Fields for configured retry settings ---
    private readonly int _maxRetries;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;

    public FileDownloader(
            IWebSystemCalls webSystemCalls,
            int? maxRetries = null,
            TimeSpan? initialRetryDelay = null,
            TimeSpan? maxRetryDelay = null)
    {
        _webSystemCalls = webSystemCalls;
        // Use provided values or fall back to defaults
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
        byte[]? expectedMd5Hash = null; // Store expected hash from headers
        long totalFileSize = 0;
        bool partialDownloadSupported = false;

        try
        {
            // --- Step 1 & 2: Get Headers & Check Capabilities (with Retry) ---
            Console.WriteLine("Attempting to get headers...");
            var headersResponse = await ExecuteWithRetryAsync(
                async (token) => await _webSystemCalls.GetHeadersAsync(contentFileUrl, token).ConfigureAwait(false),
                "GetHeaders",
                cancellationToken).ConfigureAwait(false);

            if (!headersResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to get headers. Status code: {headersResponse.StatusCode}");
                // In a real implementation, we'd likely retry here based on the status code
                return false;
            }

            using (headersResponse) // Ensure disposal
            {
                partialDownloadSupported = headersResponse.Headers.AcceptRanges.Contains("bytes");
                long? totalFileSizeNullable = headersResponse.Content.Headers.ContentLength;
                expectedMd5Hash = headersResponse.Content.Headers.ContentMD5; // Store for later check

                Console.WriteLine($"Partial download supported: {partialDownloadSupported}");
                Console.WriteLine($"Total file size: {totalFileSizeNullable?.ToString() ?? "Unknown"}");
                Console.WriteLine($"Expected MD5 Hash available: {expectedMd5Hash != null}");

                if (!totalFileSizeNullable.HasValue || totalFileSizeNullable == 0) { Console.WriteLine("Cannot determine file size."); return false; }
                totalFileSize = totalFileSizeNullable.Value;
            } // headersResponse disposed here
            // --- End Step 1 & 2 ---

            long totalBytesDownloaded = 0;

            // --- Resume Logic ---
            // Check existing file size for resuming
            if (File.Exists(localFilePath))
            {
                try
                {
                    totalBytesDownloaded = new FileInfo(localFilePath).Length;
                    Console.WriteLine($"Existing file found with size: {totalBytesDownloaded} bytes.");

                    // If local file is bigger than or equal to server file, assume complete (or corrupt)
                    if (totalBytesDownloaded >= totalFileSize)
                    {
                        // If sizes match exactly, assume done. Could add integrity check here later.
                        if (totalBytesDownloaded == totalFileSize)
                        {
                            Console.WriteLine("Existing file size matches total file size. Assuming download complete.");
                            ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded, TimeSpan.Zero);
                            // TODO: Add integrity check here even for existing file
                            return true;
                        }
                        else
                        {
                            // Local file is larger - something is wrong. Delete and start over.
                            Console.WriteLine($"Existing file size ({totalBytesDownloaded}) is larger than total size ({totalFileSize}). Deleting and restarting.");
                            TryDeleteFile(localFilePath);
                            totalBytesDownloaded = 0;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Resuming download from byte {totalBytesDownloaded}.");
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Error accessing existing file '{localFilePath}': {ex.Message}. Attempting to restart download.");
                    TryDeleteFile(localFilePath);
                    totalBytesDownloaded = 0;
                }
            }

            // Report initial progress
            ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded, TimeSpan.Zero); // TODO: Estimate time later
            // --- End Resume Logic ---

            bool downloadSuccess = false;
            // --- Step 3: Download Content --
            if (!partialDownloadSupported)
            {
                // ... (ensure totalBytesDownloaded is 0 if full download needed) ...
                if (totalBytesDownloaded > 0)
                {
                    Console.WriteLine("Partial download not supported by server, but local file exists. Deleting local file and starting full download.");
                    TryDeleteFile(localFilePath);
                    totalBytesDownloaded = 0;
                    ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded, TimeSpan.Zero); // Reset progress
                }

                downloadSuccess = await PerformFullDownloadAsync(contentFileUrl, localFilePath, totalFileSize, onProgressChanged, cancellationToken);
            }
            else
            {
                downloadSuccess = await PerformPartialDownloadAsync(contentFileUrl, localFilePath, totalFileSize, totalBytesDownloaded, onProgressChanged, cancellationToken);
            }

            // --- End Step 3 ---


            // --- Step 4: Integrity Check ---
            if (downloadSuccess)
            {
                Console.WriteLine("Download completed. Verifying integrity...");
                return await VerifyIntegrityAsync(localFilePath, expectedMd5Hash, cancellationToken);
            }
            else
            {
                Console.WriteLine("Download failed or was cancelled.");
                // Don't delete partial file on cancellation/failure to allow resume, unless integrity check fails later
                return false;
            }
            // --- End Step 4 ---
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Network error getting headers: {ex.Message}");
            // Add retry logic here in later steps
            return false;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation cancelled during header check.");
            return false;
        }
        catch (Exception ex) // Catch other potential errors
        {
            Console.WriteLine($"An unexpected error occurred: {ex.Message}");
            return false;
        }
    }

    private void ReportProgress(Action<FileProgress> onProgressChanged, long totalFileSize, long totalBytesDownloaded, TimeSpan estimatedRemaining)
    {
        double? progressPercent = null;
        if (totalFileSize > 0)
        {
            // Ensure percentage is between 0 and 100
            progressPercent = Math.Max(0.0, Math.Min(100.0, (double)totalBytesDownloaded / totalFileSize * 100.0));
        }
        onProgressChanged?.Invoke(new FileProgress(totalFileSize, totalBytesDownloaded, progressPercent, estimatedRemaining));
    }

    // Helper to attempt file deletion without throwing exceptions
    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Console.WriteLine($"Attempting to delete incomplete/corrupt file: {filePath}");
                File.Delete(filePath);
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Warning: Could not delete file '{filePath}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Warning: No permission to delete file '{filePath}': {ex.Message}");
        }
    }

    private async Task<bool> PerformFullDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize, 
        Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine("Attempting full download...");
        try
        {
            var contentResponse = await ExecuteWithRetryAsync(
                async (token) => await _webSystemCalls.DownloadContentAsync(contentFileUrl, token).ConfigureAwait(false),
                "DownloadContent (Full)",
                cancellationToken).ConfigureAwait(false);

            if (contentResponse == null || !contentResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to download content after retries. Status code: {contentResponse?.StatusCode}");
                TryDeleteFile(localFilePath); // Delete potentially corrupt file on final failure
                return false;
            }

            using (contentResponse)
            {
                if (contentResponse.Content.Headers.ContentLength.HasValue && contentResponse.Content.Headers.ContentLength != totalFileSize) 
                { totalFileSize = contentResponse.Content.Headers.ContentLength.Value; }

                Console.WriteLine($"Writing to file: {localFilePath}");
                // Use FileMode.Create to ensure we start fresh for a full download
                using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                using var contentStream = await contentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[BufferSize];
                int bytesRead;
                long currentBytesDownloaded = 0;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                    currentBytesDownloaded += bytesRead;
                    ReportProgress(onProgressChanged, totalFileSize, currentBytesDownloaded, TimeSpan.Zero);
                }
                await fileStream.FlushAsync(cancellationToken);
                Console.WriteLine("Full download stream copy complete.");
                return true; // Indicate stream copy success (integrity check happens later)
            }
        }
        catch (IOException ex) { Console.WriteLine($"File system error during full download: {ex.Message}"); TryDeleteFile(localFilePath); return false; }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancellation occurred during full download stream copy.");
            TryDeleteFile(localFilePath);
            return false;
        }
        // Other exceptions caught by main handler
    }

    private async Task<bool> PerformPartialDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize, 
        long initialBytesDownloaded, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine("Attempting partial download...");
        long totalBytesDownloaded = initialBytesDownloaded;

        // Use FileMode.OpenOrCreate to allow appending/resuming
        using var fileStream = new FileStream(localFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

        while (totalBytesDownloaded < totalFileSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long bytesRemaining = totalFileSize - totalBytesDownloaded;
            long bytesToDownload = Math.Min(bytesRemaining, DefaultChunkSize);
            long rangeFrom = totalBytesDownloaded;
            long rangeTo = rangeFrom + bytesToDownload - 1;

            Console.WriteLine($"Requesting bytes {rangeFrom}-{rangeTo}");
            HttpResponseMessage? partialResponse = null;
            try
            {
                // Retry logic for the partial download call
                partialResponse = await ExecuteWithRetryAsync(
                    async (token) => await _webSystemCalls.DownloadPartialContentAsync(contentFileUrl, rangeFrom, rangeTo, token).ConfigureAwait(false),
                    $"DownloadPartialContent ({rangeFrom}-{rangeTo})",
                    cancellationToken).ConfigureAwait(false);


                if (partialResponse == null || (partialResponse.StatusCode != HttpStatusCode.PartialContent && partialResponse.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable))
                {
                    Console.WriteLine($"Failed to download chunk {rangeFrom}-{rangeTo} after retries. Status code: {partialResponse?.StatusCode}");
                    return false; // Failed after retries
                }

                if (partialResponse.StatusCode == HttpStatusCode.PartialContent) // 206
                {
                    // Ensure stream is positioned correctly
                    fileStream.Seek(rangeFrom, SeekOrigin.Begin);

                    using var partialContentStream = await partialResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[BufferSize];
                    int bytesRead;
                    while ((bytesRead = await partialContentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        totalBytesDownloaded += bytesRead;
                        ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded, TimeSpan.Zero);
                    }
                    await fileStream.FlushAsync(cancellationToken);
                }
                else if (partialResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) // 416
                {
                    Console.WriteLine($"Server returned 416 Range Not Satisfiable. Assuming download already complete at {totalBytesDownloaded} bytes.");
                    if (totalBytesDownloaded >= totalFileSize) break; // Exit loop
                    else { Console.WriteLine("Error: Received 416 unexpectedly. Aborting."); return false; }
                }
            }
            catch (IOException ex) { Console.WriteLine($"File system error during partial download: {ex.Message}"); return false; }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Cancellation occurred during partial download stream copy.");
                // DO NOT delete partial file here - allow resume attempt later.
                return false; // Signal failure due to cancellation
            }
            // HttpRequestException handled by retry wrapper
            finally
            {
                partialResponse?.Dispose(); // Ensure response is disposed even on errors within try
            }

        } // End while loop

        // Final check after loop
        return totalBytesDownloaded == totalFileSize; // Return true if fully downloaded
    }

    private async Task<bool> VerifyIntegrityAsync(string localFilePath, byte[]? expectedMd5Hash, CancellationToken cancellationToken)
    {
        if (expectedMd5Hash == null)
        {
            Console.WriteLine("Warning: No MD5 hash provided in headers. Cannot verify integrity.");
            return true; // Proceed without verification as per requirement interpretation
        }

        try
        {
            Console.WriteLine("Calculating MD5 hash for downloaded file...");
            using var md5 = MD5.Create();
            using var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

            byte[] calculatedHash = await md5.ComputeHashAsync(fileStream, cancellationToken).ConfigureAwait(false);

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
                TryDeleteFile(localFilePath); // Delete corrupted file
                return false;
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Error reading file for integrity check: {ex.Message}");
            return false; // Can't verify if we can't read it
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation cancelled during integrity check.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error during integrity check: {ex.Message}");
            return false;
        }
    }

    private async Task<HttpResponseMessage?> ExecuteWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> action,
            string operationName,
            CancellationToken cancellationToken)
    {
        // --- Use configured delays ---
        TimeSpan currentDelay = _initialRetryDelay;
        int maxAttempts = _maxRetries; // Use configured max retries

        for (int attempt = 0; attempt <= maxAttempts; attempt++) // Use configured maxAttempts
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (attempt > 0)
                {
                    Console.WriteLine($"Retrying {operationName} (attempt {attempt}/{maxAttempts}). Waiting {currentDelay.TotalSeconds}s...");
                    await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false); // Keep using Task.Delay

                    // Exponential backoff, respecting configured max delay
                    currentDelay = TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, _maxRetryDelay.TotalSeconds));
                }

                var response = await action(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    return response;
                }

                Console.WriteLine($"{operationName} failed on attempt {attempt} with status code {response.StatusCode}.");

                if (attempt < maxAttempts) response.Dispose();
                else return response; // Return failed response after max attempts

            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"{operationName} failed on attempt {attempt} with network error: {ex.Message}");
                if (attempt >= maxAttempts) throw;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Console.WriteLine($"{operationName} failed on attempt {attempt} due to timeout.");
                if (attempt >= maxAttempts) throw;
            }
        }
        return null; // Should not be reached
    }
}