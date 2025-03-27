using System.Net;
using System.Security.Cryptography;

namespace ReliableDownloader;

internal sealed class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;
    private readonly FileDownloaderOptions _options;
    private readonly DownloadSpeedEstimator _speedEstimator = new();

    // Internal record to hold prerequisite information gathered before starting the download.
    private record DownloadPrerequisites(
        bool CanProceed,
        long TotalFileSize,
        byte[]? ExpectedMd5Hash,
        bool PartialDownloadSupported,
        long InitialBytesDownloaded,
        bool RestartRequired
    );

    public FileDownloader(
        IWebSystemCalls webSystemCalls,
        FileDownloaderOptions? options = null)
    {
        _webSystemCalls = webSystemCalls ?? throw new ArgumentNullException(nameof(webSystemCalls));
        _options = options ?? new FileDownloaderOptions();
    }

    /// <inheritdoc />
    public async Task<bool> TryDownloadFile(
        string contentFileUrl,
        string localFilePath,
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Starting download: {contentFileUrl} -> {localFilePath}");
        bool downloadAttemptSuccess = false;

        try
        {
            var prerequisites = await GetDownloadPrerequisitesAsync(contentFileUrl, localFilePath, cancellationToken);

            if (!prerequisites.CanProceed) return false;

            // Handle existing complete file (Size match AND Integrity check passes)
            if (prerequisites.InitialBytesDownloaded >= prerequisites.TotalFileSize && !prerequisites.RestartRequired)
            {
                Console.WriteLine($"[INFO] File {localFilePath} exists and matches size. Verifying integrity...");
                bool integrityOk = await VerifyIntegrityAsync(localFilePath, prerequisites.ExpectedMd5Hash, cancellationToken, logSkip: false);
                if (integrityOk)
                {
                    Console.WriteLine($"[INFO] Existing file {localFilePath} verified. Download skipped.");
                    ReportProgress(onProgressChanged, prerequisites.TotalFileSize, prerequisites.TotalFileSize); // Report 100%
                    return true;
                }
                else
                {
                    // Integrity failed, force restart
                    Console.WriteLine($"[WARN] Existing file {localFilePath} failed integrity check. Restarting download.");
                    prerequisites = prerequisites with { RestartRequired = true, InitialBytesDownloaded = 0 };
                }
            }

            // Handle conditions requiring restart
            if (prerequisites.RestartRequired)
            {
                Console.WriteLine($"[INFO] Restarting download for {localFilePath}. Deleting existing file.");
                TryDeleteFile(localFilePath);
                prerequisites = prerequisites with { InitialBytesDownloaded = 0 };
            }

            // Start estimator and report initial progress
            _speedEstimator.Start(prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded);
            ReportProgress(onProgressChanged, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded);

            try
            {
                if (prerequisites.PartialDownloadSupported)
                {
                    Console.WriteLine("[INFO] Partial download supported. Starting/Resuming download.");
                    downloadAttemptSuccess = await PerformPartialDownloadAsync(contentFileUrl, localFilePath, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded, onProgressChanged, cancellationToken);
                }
                else
                {
                    Console.WriteLine("[INFO] Partial download not supported. Performing full download.");
                    downloadAttemptSuccess = await PerformFullDownloadAsync(contentFileUrl, localFilePath, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded, onProgressChanged, cancellationToken);
                }
            }
            finally
            {
                _speedEstimator.Stop();
            }

            if (!downloadAttemptSuccess)
            {
                Console.WriteLine($"[WARN] Download stream processing failed for {localFilePath}.");
                return false;
            }

            Console.WriteLine($"[INFO] Download stream processing completed for {localFilePath}. Verifying integrity...");
            bool integrityResult = await VerifyIntegrityAsync(localFilePath, prerequisites.ExpectedMd5Hash, cancellationToken);

            if (!integrityResult)
            {
                Console.WriteLine($"[ERROR] Integrity check failed for {localFilePath}. File deleted.");
                return false;
            }

            Console.WriteLine($"[INFO] Integrity check passed for {localFilePath}.");
            ReportProgress(onProgressChanged, prerequisites.TotalFileSize, prerequisites.TotalFileSize); // Final 100%
            return true; // Success!
        }
        catch (OperationCanceledException)
        {
            _speedEstimator.Stop();
            Console.WriteLine($"[WARN] Download operation cancelled for {contentFileUrl}.");
            throw;
        }
        catch (TimeoutException ex)
        {
            _speedEstimator.Stop();
            Console.WriteLine($"[WARN] Download operation timed out for {contentFileUrl}: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            _speedEstimator.Stop();
            Console.WriteLine($"[ERROR] Unexpected error during download for {contentFileUrl}: {ex.Message}");
            return false;
        }
        finally
        {
            _speedEstimator.Stop();
        }
    }


    private async Task<DownloadPrerequisites> GetDownloadPrerequisitesAsync(string contentFileUrl, string localFilePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Getting headers for {contentFileUrl}");
        HttpResponseMessage? headersResponse = null;
        try
        {
            headersResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.GetHeadersAsync(contentFileUrl, token),
                $"GetHeaders ({contentFileUrl})",
                cancellationToken);

            if (headersResponse == null || !headersResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ERROR] Failed to get headers for {contentFileUrl}. Status: {headersResponse?.StatusCode.ToString() ?? "Unknown"}");
                return new DownloadPrerequisites(false, 0, null, false, 0, false);
            }

            bool partialSupported = headersResponse.Headers.AcceptRanges.Contains("bytes");
            long? totalSizeNullable = headersResponse.Content.Headers.ContentLength;
            byte[]? md5Hash = headersResponse.Content.Headers.ContentMD5;
            Console.WriteLine($"[DEBUG] Headers: Partial={partialSupported}, Size={totalSizeNullable?.ToString() ?? "N/A"}, MD5={(md5Hash != null ? "Present" : "Absent")}");

            if (!totalSizeNullable.HasValue || totalSizeNullable.Value < 0)
            {
                Console.WriteLine($"[ERROR] Invalid or missing Content-Length for {contentFileUrl}.");
                return new DownloadPrerequisites(false, 0, null, false, 0, false);
            }

            long totalSize = totalSizeNullable.Value;
            long initialBytes = 0;
            bool restartRequired = false;

            if (File.Exists(localFilePath))
            {
                try
                {
                    var fi = new FileInfo(localFilePath);
                    initialBytes = fi.Length;
                    Console.WriteLine($"[INFO] Existing file found: {localFilePath} ({initialBytes} bytes).");

                    if (initialBytes > totalSize)
                    {
                        Console.WriteLine($"[WARN] Existing file ({initialBytes} bytes) > server size ({totalSize}). Restarting.");
                        restartRequired = true;
                        initialBytes = 0;
                    }
                    else if (initialBytes == totalSize)
                    {
                        Console.WriteLine($"[INFO] Existing file size matches server. Will verify integrity.");
                        // Keep initialBytes as totalSize for later check
                    }
                    else // initialBytes < totalSize
                    {
                        if (partialSupported)
                        {
                            Console.WriteLine($"[INFO] Resuming download from byte {initialBytes}.");
                        }
                        else
                        {
                            Console.WriteLine($"[WARN] Partial download not supported, but smaller file exists. Restarting.");
                            restartRequired = true;
                            initialBytes = 0;
                        }
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[WARN] IO Error accessing {localFilePath}: {ex.Message}. Restarting.");
                    restartRequired = true;
                    initialBytes = 0;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"[WARN] Access Denied for {localFilePath}: {ex.Message}. Restarting.");
                    restartRequired = true;
                    initialBytes = 0;
                }
            }
            return new DownloadPrerequisites(true, totalSize, md5Hash, partialSupported, initialBytes, restartRequired);
        }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancelled while getting prerequisites for {contentFileUrl}"); throw; }
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timed out while getting prerequisites for {contentFileUrl}"); throw; }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Error getting prerequisites for {contentFileUrl}: {ex.Message}"); return new DownloadPrerequisites(false, 0, null, false, 0, false); }
        finally { headersResponse?.Dispose(); }
    }

    private async Task<bool> PerformFullDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize, long currentTotalBytesDownloaded,
        Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Performing full download: {contentFileUrl} -> {localFilePath}");
        HttpResponseMessage? contentResponse = null;
        FileStream? fileStream = null;

        // If restart was triggered, ensure progress starts from 0
        long effectiveStartBytes = 0;
        if (currentTotalBytesDownloaded > 0)
        {
            Console.WriteLine($"[DEBUG] Full download starting over existing file. Resetting progress.");
            _speedEstimator.Start(totalFileSize, 0);
            ReportProgress(onProgressChanged, totalFileSize, 0);
        }

        try
        {
            contentResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.DownloadContentAsync(contentFileUrl, token),
                $"DownloadContent (Full - {contentFileUrl})",
                cancellationToken);

            if (contentResponse == null || !contentResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ERROR] Full download failed for {contentFileUrl}. Status: {contentResponse?.StatusCode.ToString() ?? "Unknown"}");
                if (File.Exists(localFilePath)) TryDeleteFile(localFilePath);
                return false;
            }

            long actualLength = contentResponse.Content.Headers.ContentLength ?? -1;
            if (actualLength >= 0 && actualLength != totalFileSize)
            {
                Console.WriteLine($"[WARN] Content-Length changed: HEAD={totalFileSize}, GET={actualLength}. Using GET length.");
                totalFileSize = actualLength;
                _speedEstimator.Start(totalFileSize, 0); // Restart estimator with new size
                ReportProgress(onProgressChanged, totalFileSize, 0);
            }

            Console.WriteLine($"[DEBUG] Writing {totalFileSize} bytes to {localFilePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);

            using (var contentStream = await contentResponse.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[_options.BufferSize];
                int bytesRead;
                long bytesDownloadedInThisAttempt = 0;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesDownloadedInThisAttempt += bytesRead;
                    ReportProgress(onProgressChanged, totalFileSize, bytesDownloadedInThisAttempt);
                }
                await fileStream.FlushAsync(cancellationToken);
            }
            Console.WriteLine($"[INFO] Full download stream complete for {localFilePath}");
            return true;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[ERROR] File error during full download to {localFilePath}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[WARN] Cancellation during full download stream copy for {localFilePath}.");
            throw;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[WARN] Timeout during full download for {contentFileUrl}.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unexpected error during full download for {contentFileUrl}: {ex.Message}");
        }
        finally
        {
            // Ensure resources are cleaned up, attempt to delete partial file on error/cancellation
            bool shouldDelete = !(fileStream?.Length == totalFileSize); // Delete if not complete
            fileStream?.Dispose();
            contentResponse?.Dispose();
            if (shouldDelete && File.Exists(localFilePath))
            {
                await Task.Delay(50); // Brief pause before delete attempt
                TryDeleteFile(localFilePath);
            }
        }
        return false; // Return false if any exception occurred (except Cancellation/Timeout which are re-thrown)
    }

    private async Task<bool> PerformPartialDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize,
    long initialBytesDownloaded, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Performing partial download: {contentFileUrl} -> {localFilePath} from byte {initialBytesDownloaded}");
        long currentTotalBytesDownloaded = initialBytesDownloaded;
        FileStream? fileStream = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            FileMode fileOpenMode = (initialBytesDownloaded == 0) ? FileMode.Create : FileMode.OpenOrCreate;
            fileStream = new FileStream(localFilePath, fileOpenMode, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);

            if (initialBytesDownloaded > 0)
            {
                fileStream.Seek(initialBytesDownloaded, SeekOrigin.Begin);
                Console.WriteLine($"[DEBUG] Seeked to {initialBytesDownloaded} in {localFilePath} for resume.");
            }

            while (currentTotalBytesDownloaded < totalFileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long bytesRemaining = totalFileSize - currentTotalBytesDownloaded;
                long bytesToDownloadThisChunk = Math.Min(bytesRemaining, _options.ChunkSize);
                long rangeFrom = currentTotalBytesDownloaded;
                long rangeTo = rangeFrom + bytesToDownloadThisChunk - 1;

                // Prevent invalid range
                if (rangeFrom > rangeTo || rangeFrom >= totalFileSize) break;

                long bytesReadThisChunk = await DownloadChunkAsync(fileStream, contentFileUrl, rangeFrom, rangeTo, totalFileSize, currentTotalBytesDownloaded, onProgressChanged, cancellationToken);

                if (bytesReadThisChunk < 0)
                {
                    Console.WriteLine($"[WARN] Chunk {rangeFrom}-{rangeTo} failed. Partial download aborted, keeping existing file.");
                    return false; // Chunk failed after retries
                }
                else if (bytesReadThisChunk == 0 && currentTotalBytesDownloaded < totalFileSize)
                {
                    Console.WriteLine($"[WARN] Chunk {rangeFrom}-{rangeTo} returned 0 bytes unexpectedly. Assuming download complete.");
                    currentTotalBytesDownloaded = totalFileSize; // Force loop exit
                    break;
                }

                currentTotalBytesDownloaded += bytesReadThisChunk;
            }

            await fileStream.FlushAsync(cancellationToken);
            Console.WriteLine($"[INFO] Partial download stream loop finished for {localFilePath}.");

            fileStream.Dispose(); // Release file handle before final size check
            fileStream = null;

            await Task.Delay(50); // Filesystem delay

            long finalSize = new FileInfo(localFilePath).Length;
            if (finalSize < totalFileSize)
            {
                Console.WriteLine($"[WARN] Partial download finished, but file size ({finalSize}) < server size ({totalFileSize}).");
                return false; // Incomplete
            }

            Console.WriteLine($"[INFO] Partial download complete based on size ({finalSize} >= {totalFileSize}).");
            return true;
        }
        catch (IOException ex) { Console.WriteLine($"[ERROR] File error during partial download to {localFilePath}: {ex.Message}"); return false; }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancellation during partial download for {localFilePath}. Keeping partial file."); throw; } 
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout during partial download for {contentFileUrl}. Keeping partial file."); throw; } 
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during partial download for {contentFileUrl}: {ex.Message}"); return false; }
        finally { fileStream?.Dispose(); }
    }


    private async Task<long> DownloadChunkAsync(FileStream fileStream, string url, long rangeFrom, long rangeTo, long totalFileSize, long currentTotalBeforeChunk, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Requesting chunk {rangeFrom}-{rangeTo} for {url}");
        HttpResponseMessage? partialResponse = null;
        try
        {
            partialResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.DownloadPartialContentAsync(url, rangeFrom, rangeTo, token),
                $"DownloadChunk ({rangeFrom}-{rangeTo})",
                cancellationToken);

            if (partialResponse == null || (!partialResponse.IsSuccessStatusCode && partialResponse.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                Console.WriteLine($"[ERROR] Chunk {rangeFrom}-{rangeTo} failed. Status: {partialResponse?.StatusCode.ToString() ?? "Unknown"}");
                return -1; // Indicate failure
            }

            if (partialResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Console.WriteLine($"[WARN] Received 416 for chunk {rangeFrom}-{rangeTo}. Assuming end of file.");
                return 0; // Treat as 0 bytes read
            }

            if (partialResponse.IsSuccessStatusCode)
            {
                if (partialResponse.StatusCode == HttpStatusCode.OK)
                {
                    Console.WriteLine($"[WARN] Received 200 OK for chunk {rangeFrom}-{rangeTo}. Server might ignore range.");
                    // Continue processing stream, but be aware resume might be broken if server sent full file.
                }

                // Ensure stream is positioned correctly before writing
                if (fileStream.Position != rangeFrom)
                {
                    Console.WriteLine($"[DEBUG] Stream pos {fileStream.Position}, seeking to {rangeFrom} for chunk write.");
                    fileStream.Seek(rangeFrom, SeekOrigin.Begin);
                }

                using (var partialContentStream = await partialResponse.Content.ReadAsStreamAsync(cancellationToken))
                {
                    var buffer = new byte[_options.BufferSize];
                    int bytesRead;
                    long bytesReadThisChunk = 0;
                    while ((bytesRead = await partialContentStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        bytesReadThisChunk += bytesRead;
                        ReportProgress(onProgressChanged, totalFileSize, currentTotalBeforeChunk + bytesReadThisChunk);
                    }
                    Console.WriteLine($"[TRACE] Finished writing {bytesReadThisChunk} bytes for chunk {rangeFrom}-{rangeTo}.");
                    return bytesReadThisChunk;
                }
            }
            else
            {
                Console.WriteLine($"[ERROR] Unexpected status {partialResponse.StatusCode} for chunk {rangeFrom}-{rangeTo}.");
                return -1;
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancelled during chunk {rangeFrom}-{rangeTo}."); throw; }
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout during chunk {rangeFrom}-{rangeTo}."); throw; }
        catch (IOException ex) { Console.WriteLine($"[ERROR] IO error processing chunk {rangeFrom}-{rangeTo}: {ex.Message}"); return -1; }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error processing chunk {rangeFrom}-{rangeTo}: {ex.Message}"); return -1; }
        finally { partialResponse?.Dispose(); }
    }

    private void ReportProgress(Action<FileProgress> onProgressChanged, long totalFileSize, long totalBytesDownloaded)
    {
        totalBytesDownloaded = Math.Min(totalBytesDownloaded, totalFileSize); // Clamp to total size
        _speedEstimator.UpdateBytesDownloaded(totalBytesDownloaded);

        double? progressPercent = (totalFileSize > 0)
            ? Math.Max(0.0, Math.Min(100.0, (double)totalBytesDownloaded / totalFileSize * 100.0))
            : (totalBytesDownloaded == 0 ? 0.0 : null); // Handle 0 or unknown total size

        TimeSpan? estimatedRemaining = _speedEstimator.EstimateRemainingTime();

        try
        {
            onProgressChanged?.Invoke(new FileProgress(totalFileSize, totalBytesDownloaded, progressPercent, estimatedRemaining));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Error in onProgressChanged callback: {ex.Message}");
        }
    }

    private async Task<bool> VerifyIntegrityAsync(string localFilePath, byte[]? expectedMd5Hash, CancellationToken cancellationToken, bool logSkip = false)
    {
        if (expectedMd5Hash == null)
        {
            if (!logSkip) Console.WriteLine($"[INFO] No MD5 hash provided, skipping integrity check for {localFilePath}.");
            return true;
        }

        if (!File.Exists(localFilePath))
        {
            Console.WriteLine($"[ERROR] Integrity check failed: File {localFilePath} not found.");
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!logSkip) Console.WriteLine($"[INFO] Calculating MD5 for {localFilePath}");

            byte[] calculatedHash;
            int hashBufferSize = Math.Min(_options.BufferSize, 1 * 1024 * 1024); // Max 1MB buffer for hashing

            using (var md5 = MD5.Create())
            using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, hashBufferSize, FileOptions.Asynchronous))
            {
                calculatedHash = await md5.ComputeHashAsync(fs, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (expectedMd5Hash.SequenceEqual(calculatedHash))
            {
                if (!logSkip) Console.WriteLine($"[INFO] Integrity check passed: MD5 matches for {localFilePath}.");
                return true;
            }
            else
            {
                if (!logSkip)
                {
                    Console.WriteLine($"[ERROR] Integrity check failed: MD5 mismatch for {localFilePath}!");
                    Console.WriteLine($"  Expected: {Convert.ToHexString(expectedMd5Hash)}");
                    Console.WriteLine($"  Actual:   {Convert.ToHexString(calculatedHash)}");
                    TryDeleteFile(localFilePath); // Delete corrupted file
                }
                return false;
            }
        }
        catch (IOException ex) { Console.WriteLine($"[ERROR] IO Error reading {localFilePath} for integrity check: {ex.Message}"); return false; }
        catch (UnauthorizedAccessException ex) { Console.WriteLine($"[ERROR] Permission denied reading {localFilePath} for integrity check: {ex.Message}"); return false; }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancelled during integrity check for {localFilePath}."); throw; }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during integrity check for {localFilePath}: {ex.Message}"); return false; }
    }

    private async Task<HttpResponseMessage?> ExecuteWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> action,
            string operationName,
            CancellationToken cancellationToken)
    {
        TimeSpan currentDelay = _options.InitialRetryDelay;
        HttpResponseMessage? response = null;

        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (attempt > 0)
                {
                    Console.WriteLine($"[DEBUG] Retrying {operationName} in {currentDelay.TotalSeconds:F1}s (Attempt {attempt}/{_options.MaxRetries})...");
                    await Task.Delay(currentDelay, cancellationToken);
                    currentDelay = TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, _options.MaxRetryDelay.TotalSeconds));
                }

                response = await action(cancellationToken);

                // Treat success or RangeNotSatisfiable (for chunks) as success here
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    return response;
                }

                bool shouldRetry = response.StatusCode >= HttpStatusCode.InternalServerError || // 5xx
                                   response.StatusCode == HttpStatusCode.RequestTimeout ||       // 408
                                   response.StatusCode == HttpStatusCode.TooManyRequests;       // 429

                if (!shouldRetry)
                {
                    Console.WriteLine($"[WARN] {operationName} failed attempt {attempt}, non-retriable status {response.StatusCode}.");
                    return response; // Return the failure response
                }

                Console.WriteLine($"[WARN] {operationName} failed attempt {attempt}, retriable status {response.StatusCode}.");

                if (attempt < _options.MaxRetries)
                {
                    response.Dispose();
                    response = null;
                }
                // Keep last response if it's the final attempt
            }
            catch (HttpRequestException ex) // Network errors
            {
                response?.Dispose(); response = null;
                Console.WriteLine($"[WARN] {operationName} attempt {attempt} failed (Network Error): {ex.Message}");
                if (attempt >= _options.MaxRetries) throw;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested) // HttpClient timeouts
            {
                response?.Dispose(); response = null;
                Console.WriteLine($"[WARN] {operationName} attempt {attempt} timed out: {ex.InnerException?.Message ?? ex.Message}");
                if (attempt >= _options.MaxRetries) throw new TimeoutException($"'{operationName}' timed out after {_options.MaxRetries + 1} attempts.", ex);
            }
            catch (OperationCanceledException) // Explicit cancellation
            {
                response?.Dispose();
                Console.WriteLine($"[WARN] Cancelled by token during {operationName} attempt {attempt}.");
                throw;
            }
            catch (Exception ex) // Unexpected errors
            {
                response?.Dispose(); response = null;
                Console.WriteLine($"[ERROR] Unexpected error during {operationName} attempt {attempt}: {ex.GetType().Name} - {ex.Message}");
                if (attempt >= _options.MaxRetries) throw;
            }
        }

        Console.WriteLine($"[ERROR] {operationName} failed after {_options.MaxRetries + 1} attempts.");
        return response; // Return the last response (null if exception on last attempt)
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Console.WriteLine($"[INFO] Deleting file: {filePath}");
                File.Delete(filePath);
            }
        }
        catch (IOException ex) { Console.WriteLine($"[WARN] Failed to delete '{filePath}': {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Console.WriteLine($"[WARN] No permission to delete '{filePath}': {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error deleting '{filePath}': {ex.Message}"); }
    }
}