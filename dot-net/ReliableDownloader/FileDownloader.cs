using System.Net;
using System.Security.Cryptography;

namespace ReliableDownloader;

/// <summary>
/// Main implementation for reliable file downloading.
/// </summary>
internal sealed class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;
    private readonly FileDownloaderOptions _options;
    private readonly DownloadSpeedEstimator _speedEstimator = new();

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
        Console.WriteLine($"[DEBUG] FileDownloader initialized with options: MaxRetries={_options.MaxRetries}, InitialDelay={_options.InitialRetryDelay}, MaxDelay={_options.MaxRetryDelay}, ChunkSize={_options.ChunkSize}, BufferSize={_options.BufferSize}");
    }

    /// <inheritdoc />
    public async Task<bool> TryDownloadFile(
        string contentFileUrl,
        string localFilePath,
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Starting download attempt for URL: {contentFileUrl} to file: {localFilePath}");
        bool downloadAttemptSuccess = false;

        try
        {
            var prerequisites = await GetDownloadPrerequisitesAsync(contentFileUrl, localFilePath, cancellationToken).ConfigureAwait(false);

            if (!prerequisites.CanProceed) return false;
            if (prerequisites.InitialBytesDownloaded >= prerequisites.TotalFileSize && !prerequisites.RestartRequired) return true;
            if (prerequisites.RestartRequired) TryDeleteFile(localFilePath);

            _speedEstimator.Start(prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded);
            ReportProgress(onProgressChanged, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded);

            try
            {
                if (prerequisites.PartialDownloadSupported)
                {
                    downloadAttemptSuccess = await PerformPartialDownloadAsync(contentFileUrl, localFilePath, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded, onProgressChanged, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    long bytesToStartFrom = prerequisites.InitialBytesDownloaded > 0 ? 0 : prerequisites.InitialBytesDownloaded;
                    if (prerequisites.InitialBytesDownloaded > 0) { _speedEstimator.Start(prerequisites.TotalFileSize, 0); ReportProgress(onProgressChanged, prerequisites.TotalFileSize, 0); }
                    downloadAttemptSuccess = await PerformFullDownloadAsync(contentFileUrl, localFilePath, prerequisites.TotalFileSize, bytesToStartFrom, onProgressChanged, cancellationToken).ConfigureAwait(false);
                }
            }
            finally { _speedEstimator.Stop(); }

            if (!downloadAttemptSuccess)
            {
                Console.WriteLine($"[WARN] Download did not complete successfully for {localFilePath} (Perform* returned false).");
                return false;
            }

            Console.WriteLine($"[INFO] Download stream copy completed for {localFilePath}. Verifying integrity...");
            bool integrityResult = await VerifyIntegrityAsync(localFilePath, prerequisites.ExpectedMd5Hash, cancellationToken).ConfigureAwait(false);
            if (!integrityResult) { Console.WriteLine($"[ERROR] Integrity check failed for {localFilePath}. File has been deleted."); return false; }
            return true; // Success!
        }
        catch (OperationCanceledException ex) { _speedEstimator.Stop(); Console.WriteLine($"[WARN] Download operation cancelled for URL: {contentFileUrl}. Exception Type: {ex.GetType().Name}"); throw; }
        catch (TimeoutException ex) { _speedEstimator.Stop(); Console.WriteLine($"[WARN] Download operation timed out for URL: {contentFileUrl}. Exception Type: {ex.GetType().Name}"); throw; }
        catch (Exception ex) { _speedEstimator.Stop(); Console.WriteLine($"[ERROR] An unexpected error occurred during download process for URL {contentFileUrl}: {ex.Message}"); return false; }
        finally { if (_speedEstimator != null) _speedEstimator.Stop(); }
    }


    private async Task<DownloadPrerequisites> GetDownloadPrerequisitesAsync(string contentFileUrl, string localFilePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Attempting to get headers for {contentFileUrl}");
        HttpResponseMessage? headersResponse = null;
        try
        {
            headersResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.GetHeadersAsync(contentFileUrl, token), // <<< Ensure lambda is here
                $"GetHeaders ({contentFileUrl})",
                cancellationToken).ConfigureAwait(false); // Can throw OCE/Timeout

            if (headersResponse == null || !headersResponse.IsSuccessStatusCode) { /* ... handle failure ... */ return new DownloadPrerequisites(false, 0, null, false, 0, false); }

            bool partialSupported = headersResponse.Headers.AcceptRanges.Contains("bytes");
            long? totalSizeNullable = headersResponse.Content.Headers.ContentLength; byte[]? md5Hash = headersResponse.Content.Headers.ContentMD5;
            Console.WriteLine($"[DEBUG] Headers received for {contentFileUrl}: PartialSupported={partialSupported}, Size={totalSizeNullable?.ToString() ?? "Unknown"}, MD5Present={md5Hash != null}");
            if (!totalSizeNullable.HasValue || totalSizeNullable.Value < 0) { /* ... handle failure ... */ return new DownloadPrerequisites(false, 0, null, false, 0, false); }

            long totalSize = totalSizeNullable.Value; long initialBytes = 0; bool restartRequired = false;
            if (File.Exists(localFilePath))
            {
                try
                {
                    var fi = new FileInfo(localFilePath); initialBytes = fi.Length; Console.WriteLine($"[INFO] Existing file {localFilePath} found with size: {initialBytes} bytes.");
                    if (initialBytes >= totalSize)
                    {
                        if (initialBytes == totalSize) { bool ok = md5Hash != null ? await VerifyIntegrityAsync(localFilePath, md5Hash, cancellationToken, logSkip: true).ConfigureAwait(false) : true; if (ok) return new DownloadPrerequisites(true, totalSize, md5Hash, partialSupported, totalSize, false); else { restartRequired = true; initialBytes = 0; } }
                        else { restartRequired = true; initialBytes = 0; }
                    }
                    else if (!partialSupported) { restartRequired = true; initialBytes = 0; }
                    else { Console.WriteLine($"[INFO] Resuming download from byte {initialBytes}."); }
                }
                catch (IOException ex) { Console.WriteLine($"[WARN] IO Error accessing existing file {localFilePath}: {ex.Message}. Marking for restart."); restartRequired = true; initialBytes = 0; }
                catch (UnauthorizedAccessException ex) { Console.WriteLine($"[WARN] Access Denied accessing existing file {localFilePath}: {ex.Message}. Marking for restart."); restartRequired = true; initialBytes = 0; }
            }
            return new DownloadPrerequisites(true, totalSize, md5Hash, partialSupported, initialBytes, restartRequired);
        }
        // Let OCE/TimeoutException propagate
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Operation cancelled while getting prerequisites for {contentFileUrl}"); throw; }
        catch (TimeoutException) { Console.WriteLine($"[WARN] Operation timed out while getting prerequisites for {contentFileUrl}"); throw; }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error getting prerequisites for {contentFileUrl}: {ex.Message}"); return new DownloadPrerequisites(false, 0, null, false, 0, false); }
        finally { headersResponse?.Dispose(); }
    }


    private async Task<bool> PerformFullDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize, long currentTotalBytesDownloaded,
        Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Attempting full download for {contentFileUrl} to {localFilePath}");
        HttpResponseMessage? contentResponse = null; FileStream? fileStream = null;
        try
        {
            // ***** FIX: Restore missing lambda *****
            contentResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.DownloadContentAsync(contentFileUrl, token), // <<< Ensure lambda is here
                $"DownloadContent (Full - {contentFileUrl})",
                cancellationToken).ConfigureAwait(false); // Can throw OCE/Timeout

            if (contentResponse == null || !contentResponse.IsSuccessStatusCode) { /* ... return false ... */ return false; }

            long actualLength = contentResponse.Content.Headers.ContentLength ?? -1;
            if (actualLength != totalFileSize && actualLength >= 0) { /* ... adjust totalFileSize ... */ totalFileSize = actualLength; _speedEstimator.Start(totalFileSize, currentTotalBytesDownloaded); }

            Console.WriteLine($"[DEBUG] Writing {totalFileSize} bytes to file: {localFilePath}");
            fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);
            using (var contentStream = await contentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            { // Can throw OCE
                var buffer = new byte[_options.BufferSize]; int bytesRead; long dlAttempt = 0;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                { // Can throw OCE
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false); // Can throw OCE
                    dlAttempt += bytesRead; ReportProgress(onProgressChanged, totalFileSize, currentTotalBytesDownloaded + dlAttempt);
                }
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false); // Can throw OCE
            }
            Console.WriteLine($"[INFO] Full download stream copy complete for {localFilePath}"); return true; // Success
        }
        catch (IOException ex) { Console.WriteLine($"[ERROR] File system error during full download to {localFilePath}: {ex.Message}"); return false; } // Return false on IO
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancellation occurred during full download stream copy for {localFilePath}."); throw; } // Re-throw OCE
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout occurred during full download for {contentFileUrl}."); throw; } // Re-throw Timeout
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during full download for {contentFileUrl}: {ex.Message}"); return false; } // Return false on unexpected
        finally { fileStream?.Dispose(); if (cancellationToken.IsCancellationRequested) { TryDeleteFile(localFilePath); } contentResponse?.Dispose(); }
    }


    private async Task<bool> PerformPartialDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize,
        long initialBytesDownloaded, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Attempting partial download/resume for {contentFileUrl} to {localFilePath} starting from {initialBytesDownloaded}");
        long currentTotalBytesDownloaded = initialBytesDownloaded; FileStream? fileStream = null;
        try
        {
            fileStream = new FileStream(localFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);
            while (currentTotalBytesDownloaded < totalFileSize)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check before chunk
                long bytesRemaining = totalFileSize - currentTotalBytesDownloaded; long bytesToDownload = Math.Min(bytesRemaining, _options.ChunkSize); long rangeFrom = currentTotalBytesDownloaded; long rangeTo = rangeFrom + bytesToDownload - 1;
                if (rangeFrom > rangeTo || rangeFrom >= totalFileSize) break;

                long bytesReadThisChunk = await DownloadChunkAsync(fileStream, contentFileUrl, rangeFrom, rangeTo, totalFileSize, currentTotalBytesDownloaded, onProgressChanged, cancellationToken).ConfigureAwait(false); // Can throw OCE/Timeout

                if (bytesReadThisChunk < 0) return false; // Chunk failed, keep partial
                else if (bytesReadThisChunk == 0 && currentTotalBytesDownloaded < totalFileSize) { currentTotalBytesDownloaded = totalFileSize; break; } // Assume complete
                currentTotalBytesDownloaded += bytesReadThisChunk; Console.WriteLine($"[TRACE] Chunk {rangeFrom}-{rangeTo} downloaded. Total downloaded: {currentTotalBytesDownloaded}");
            }
            if (fileStream != null) await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false); // Can throw OCE
            Console.WriteLine($"[INFO] Partial download stream copy loop finished for {localFilePath}. Total bytes: {currentTotalBytesDownloaded}");
            if (currentTotalBytesDownloaded < totalFileSize) { Console.WriteLine($"[WARN] Partial download loop finished for {localFilePath}, but downloaded bytes ({currentTotalBytesDownloaded}) is less than total size ({totalFileSize})."); return false; }
            return true; // Completed successfully
        }
        catch (IOException ex) { Console.WriteLine($"[ERROR] File system error during partial download to {localFilePath}: {ex.Message}"); return false; } // Keep partial
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancellation occurred during partial download setup or loop for {localFilePath}. Keeping partial file."); throw; } // Re-throw OCE
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout occurred during partial download for {contentFileUrl}. Keeping partial file."); throw; } // Re-throw Timeout
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during partial download for {contentFileUrl}: {ex.Message}"); return false; } // Keep partial
        finally { fileStream?.Dispose(); }
    }


    private async Task<long> DownloadChunkAsync(FileStream fileStream, string url, long rangeFrom, long rangeTo, long totalFileSize, long currentTotalBeforeChunk, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Requesting chunk {rangeFrom}-{rangeTo} for {url}");
        HttpResponseMessage? partialResponse = null;
        try
        {
            // ***** FIX: Restore missing lambda *****
            partialResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.DownloadPartialContentAsync(url, rangeFrom, rangeTo, token), // <<< Ensure lambda is here
                $"DownloadPartialContent ({rangeFrom}-{rangeTo} - {url})",
                cancellationToken).ConfigureAwait(false); // Can throw OCE/TimeoutException

            if (partialResponse == null || (!partialResponse.IsSuccessStatusCode && partialResponse.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)) return -1; // Failed after retry
            if (partialResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return 0; // Assume complete

            if (partialResponse.StatusCode == HttpStatusCode.PartialContent)
            {
                Console.WriteLine($"[TRACE] Received 206 Partial Content for chunk {rangeFrom}-{rangeTo} for {url}. Writing to stream."); cancellationToken.ThrowIfCancellationRequested();
                fileStream.Seek(rangeFrom, SeekOrigin.Begin); // Can throw IO
                using (var partialContentStream = await partialResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                { // Can throw OCE
                    var buffer = new byte[_options.BufferSize]; int bytesRead; long bytesReadThisChunk = 0;
                    while ((bytesRead = await partialContentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    { // Can throw OCE
                        cancellationToken.ThrowIfCancellationRequested(); await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false); // Can throw OCE, IO
                        bytesReadThisChunk += bytesRead; ReportProgress(onProgressChanged, totalFileSize, currentTotalBeforeChunk + bytesReadThisChunk);
                    }
                    Console.WriteLine($"[TRACE] Finished writing {bytesReadThisChunk} bytes for chunk {rangeFrom}-{rangeTo} for {url}."); return bytesReadThisChunk; // Success
                }
            }
            else { return -1; } // Unexpected status
        }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Operation cancelled during processing of chunk {rangeFrom}-{rangeTo} for {url}"); throw; } // Re-throw OCE
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout occurred downloading chunk {rangeFrom}-{rangeTo} for {url}"); throw; } // Re-throw Timeout
        catch (IOException ex) { Console.WriteLine($"[ERROR] IO error processing chunk {rangeFrom}-{rangeTo} for {url}: {ex.Message}"); return -1; } // Return -1 on IO
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error downloading chunk {rangeFrom}-{rangeTo} for {url}: {ex.Message}"); return -1; } // Return -1 on unexpected
        finally { partialResponse?.Dispose(); }
    }


    private void ReportProgress(Action<FileProgress> onProgressChanged, long totalFileSize, long totalBytesDownloaded)
    {
        // Unchanged
        totalBytesDownloaded = Math.Min(totalBytesDownloaded, totalFileSize); _speedEstimator.UpdateBytesDownloaded(totalBytesDownloaded);
        double? progressPercent = (totalFileSize > 0) ? Math.Max(0.0, Math.Min(100.0, (double)totalBytesDownloaded / totalFileSize * 100.0)) : (totalBytesDownloaded > 0 ? (double?)null : 0.0);
        TimeSpan? estimatedRemaining = _speedEstimator.EstimateRemainingTime();
        try { onProgressChanged?.Invoke(new FileProgress(totalFileSize, totalBytesDownloaded, progressPercent, estimatedRemaining)); }
        catch (Exception ex) { Console.WriteLine($"[WARN] Error occurred within the onProgressChanged callback: {ex.Message}"); }
    }


    private async Task<bool> VerifyIntegrityAsync(string localFilePath, byte[]? expectedMd5Hash, CancellationToken cancellationToken, bool logSkip = false)
    {
        // This method correctly re-throws OCE
        if (expectedMd5Hash == null) { if (!logSkip) Console.WriteLine($"[INFO] MD5 header not provided. Skipping integrity check for {localFilePath}."); return true; }
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); // Check before
            if (!logSkip) Console.WriteLine($"[INFO] Calculating MD5 hash for {localFilePath}"); byte[] calculatedHash;
            using (var md5 = MD5.Create()) using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize, FileOptions.Asynchronous)) { calculatedHash = await md5.ComputeHashAsync(fs).ConfigureAwait(false); }
            cancellationToken.ThrowIfCancellationRequested(); // Check after
            if (expectedMd5Hash.SequenceEqual(calculatedHash)) { if (!logSkip) Console.WriteLine($"[INFO] Integrity check passed for {localFilePath}: MD5 hashes match."); return true; }
            else { if (!logSkip) { Console.WriteLine($"[ERROR] Integrity check failed for {localFilePath}: MD5 hashes do not match!"); Console.WriteLine($"Expected: {BitConverter.ToString(expectedMd5Hash).Replace("-", "")}"); Console.WriteLine($"Calculated: {BitConverter.ToString(calculatedHash).Replace("-", "")}"); TryDeleteFile(localFilePath); } return false; }
        }
        catch (IOException ex) { Console.WriteLine($"[ERROR] IO Error reading file {localFilePath} for integrity check: {ex.Message}"); return false; }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Operation cancelled during integrity check for {localFilePath}."); throw; } // Re-throw
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during integrity check for {localFilePath}: {ex.Message}"); return false; }
    }


    private async Task<HttpResponseMessage?> ExecuteWithRetryAsync(
             Func<CancellationToken, Task<HttpResponseMessage>> action,
             string operationName,
             CancellationToken cancellationToken)
    {
        // This method correctly re-throws OCE/TimeoutException
        TimeSpan currentDelay = _options.InitialRetryDelay; HttpResponseMessage? response = null;
        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (attempt > 0) { await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false); currentDelay = TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, _options.MaxRetryDelay.TotalSeconds)); }
                Console.WriteLine($"[TRACE] Executing {operationName}, attempt {attempt}"); response = await action(cancellationToken).ConfigureAwait(false); // Pass CT
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return response;
                bool shouldRetry = response.StatusCode >= HttpStatusCode.InternalServerError || response.StatusCode == HttpStatusCode.RequestTimeout || response.StatusCode == HttpStatusCode.TooManyRequests;
                if (!shouldRetry) return response; Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} with retriable status code {response.StatusCode}."); if (attempt < _options.MaxRetries) { response.Dispose(); response = null; }
            }
            catch (HttpRequestException ex) { response?.Dispose(); response = null; if (attempt >= _options.MaxRetries) throw; Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} with network error: {ex.Message}"); }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested) { response?.Dispose(); response = null; if (attempt >= _options.MaxRetries) throw new TimeoutException($"Operation '{operationName}' timed out after {_options.MaxRetries + 1} attempts.", ex); Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} due to a timeout: {ex.Message}"); }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) { response?.Dispose(); Console.WriteLine($"[WARN] Operation cancelled by token during {operationName} on attempt {attempt}."); throw; }
            catch (Exception ex) { response?.Dispose(); response = null; if (attempt >= _options.MaxRetries) throw; Console.WriteLine($"[ERROR] Unexpected error during {operationName} on attempt {attempt}: {ex.Message}"); }
        }
        return response;
    }


    private void TryDeleteFile(string filePath)
    {
        // Unchanged
        try { if (File.Exists(filePath)) { Console.WriteLine($"[INFO] Attempting to delete file: {filePath}"); File.Delete(filePath); Console.WriteLine($"[DEBUG] Successfully deleted file: {filePath}"); } }
        catch (IOException ex) { Console.WriteLine($"[WARN] Could not delete file '{filePath}': {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Console.WriteLine($"[WARN] No permission to delete file '{filePath}': {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error deleting file '{filePath}': {ex.Message}"); }
    }
}