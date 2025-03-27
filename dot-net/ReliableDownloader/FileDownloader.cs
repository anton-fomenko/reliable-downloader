using System.Net;
using System.Security.Cryptography;

namespace ReliableDownloader;

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

            // Handle existing complete file (Size match AND Integrity check passes)
            if (prerequisites.InitialBytesDownloaded >= prerequisites.TotalFileSize && !prerequisites.RestartRequired)
            {
                Console.WriteLine($"[INFO] File {localFilePath} already exists and matches server size. Verifying integrity (if possible)...");
                bool integrityOk = await VerifyIntegrityAsync(localFilePath, prerequisites.ExpectedMd5Hash, cancellationToken, logSkip: false).ConfigureAwait(false);
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

            // Handle conditions requiring restart (Local > Server, Integrity Fail, Partial Not Supported + Local Exists<Server)
            if (prerequisites.RestartRequired)
            {
                Console.WriteLine($"[INFO] Restarting download for {localFilePath}. Deleting existing file.");
                TryDeleteFile(localFilePath);
                // Reset initial bytes for the download logic that follows
                prerequisites = prerequisites with { InitialBytesDownloaded = 0 };
            }

            // Start estimator and report initial progress (which might be 0 if restarting)
            _speedEstimator.Start(prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded);
            ReportProgress(onProgressChanged, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded);

            try
            {
                // Logic preferring partial if supported
                if (prerequisites.PartialDownloadSupported)
                {
                    if (prerequisites.RestartRequired)
                    {
                        Console.WriteLine("[INFO] Partial download supported. Performing restart using partial download mechanism from byte 0.");
                    }
                    downloadAttemptSuccess = await PerformPartialDownloadAsync(contentFileUrl, localFilePath, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded, onProgressChanged, cancellationToken).ConfigureAwait(false);
                }
                else // Partial download not supported
                {
                    Console.WriteLine("[INFO] Partial download not supported. Performing full download.");
                    downloadAttemptSuccess = await PerformFullDownloadAsync(contentFileUrl, localFilePath, prerequisites.TotalFileSize, prerequisites.InitialBytesDownloaded, onProgressChanged, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _speedEstimator.Stop();
            }

            if (!downloadAttemptSuccess)
            {
                Console.WriteLine($"[WARN] Download method did not complete successfully for {localFilePath}.");
                return false;
            }

            Console.WriteLine($"[INFO] Download stream processing completed for {localFilePath}. Verifying integrity...");
            bool integrityResult = await VerifyIntegrityAsync(localFilePath, prerequisites.ExpectedMd5Hash, cancellationToken).ConfigureAwait(false);

            if (!integrityResult)
            {
                Console.WriteLine($"[ERROR] Integrity check failed for {localFilePath}. File has been deleted.");
                return false;
            }

            Console.WriteLine($"[INFO] Integrity check passed for {localFilePath}.");
            ReportProgress(onProgressChanged, prerequisites.TotalFileSize, prerequisites.TotalFileSize);
            return true; // Success!
        }
        catch (OperationCanceledException ex)
        {
            _speedEstimator.Stop();
            Console.WriteLine($"[WARN] Download operation cancelled for URL: {contentFileUrl}. Exception Type: {ex.GetType().Name}");
            throw; // Re-throw OCE
        }
        catch (TimeoutException ex)
        {
            _speedEstimator.Stop();
            Console.WriteLine($"[WARN] Download operation timed out for URL: {contentFileUrl}. Exception Type: {ex.GetType().Name}");
            throw; // Re-throw TimeoutException
        }
        catch (Exception ex)
        {
            _speedEstimator.Stop();
            Console.WriteLine($"[ERROR] An unexpected error occurred during download process for URL {contentFileUrl}: {ex.Message}");
            return false; // Indicate failure
        }
        finally
        {
            if (_speedEstimator != null)
            {
                _speedEstimator.Stop();
            }
        }
    }

    private async Task<DownloadPrerequisites> GetDownloadPrerequisitesAsync(string contentFileUrl, string localFilePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Attempting to get headers for {contentFileUrl}");
        HttpResponseMessage? headersResponse = null;
        try
        {
            headersResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.GetHeadersAsync(contentFileUrl, token),
                $"GetHeaders ({contentFileUrl})",
                cancellationToken).ConfigureAwait(false); // Can throw OCE/Timeout

            if (headersResponse == null || !headersResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ERROR] Failed to get headers for {contentFileUrl} after retries. Status: {headersResponse?.StatusCode.ToString() ?? "Unknown"}");
                return new DownloadPrerequisites(false, 0, null, false, 0, false);
            }

            bool partialSupported = headersResponse.Headers.AcceptRanges.Contains("bytes");
            long? totalSizeNullable = headersResponse.Content.Headers.ContentLength;
            byte[]? md5Hash = headersResponse.Content.Headers.ContentMD5;
            Console.WriteLine($"[DEBUG] Headers received for {contentFileUrl}: PartialSupported={partialSupported}, Size={totalSizeNullable?.ToString() ?? "Unknown"}, MD5Present={md5Hash != null}");

            if (!totalSizeNullable.HasValue || totalSizeNullable.Value < 0)
            {
                Console.WriteLine($"[ERROR] Invalid or missing Content-Length header for {contentFileUrl}.");
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
                    Console.WriteLine($"[INFO] Existing file {localFilePath} found with size: {initialBytes} bytes.");

                    if (initialBytes > totalSize)
                    {
                        // Local file is larger than server file, indicates corruption or wrong file
                        Console.WriteLine($"[WARN] Existing file size ({initialBytes}) is larger than server file size ({totalSize}). Restarting download.");
                        restartRequired = true;
                        initialBytes = 0; // Will start from 0 after delete
                    }
                    else if (initialBytes == totalSize)
                    {
                        // File exists and size matches. Don't restart yet, but let the main method verify integrity.
                        Console.WriteLine($"[INFO] Existing file size matches server size. Will verify integrity.");
                        // Keep initialBytes as totalSize for the main method's check.
                    }
                    else // initialBytes < totalSize
                    {
                        if (partialSupported)
                        {
                            Console.WriteLine($"[INFO] Resuming download from byte {initialBytes}.");
                        }
                        else
                        {
                            // Partial not supported, but local file exists and is smaller. Must restart.
                            Console.WriteLine($"[WARN] Partial download not supported, but smaller file exists. Restarting download.");
                            restartRequired = true;
                            initialBytes = 0; // Will start from 0 after delete
                        }
                    }
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[WARN] IO Error accessing existing file {localFilePath}: {ex.Message}. Marking for restart.");
                    restartRequired = true;
                    initialBytes = 0;
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"[WARN] Access Denied accessing existing file {localFilePath}: {ex.Message}. Marking for restart.");
                    restartRequired = true;
                    initialBytes = 0;
                }
            }
            // Note: initialBytes will be > 0 only if resuming is possible (partial supported, local < server, no errors)
            // It will be 0 if restarting or downloading for the first time.
            // It will be == totalSize if size matches (pending integrity check).
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
        HttpResponseMessage? contentResponse = null;
        FileStream? fileStream = null;

        long effectiveStartBytes = 0;
        if (currentTotalBytesDownloaded > 0)
        {
            Console.WriteLine($"[DEBUG] Full download initiated, but existing bytes {currentTotalBytesDownloaded} detected (likely restart). Ensuring download starts from byte 0.");
            _speedEstimator.Start(totalFileSize, 0);
            ReportProgress(onProgressChanged, totalFileSize, 0);
        }

        try
        {
            contentResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.DownloadContentAsync(contentFileUrl, token),
                $"DownloadContent (Full - {contentFileUrl})",
                cancellationToken).ConfigureAwait(false);

            if (contentResponse == null || !contentResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[ERROR] Failed to download content fully for {contentFileUrl} after retries. Status: {contentResponse?.StatusCode.ToString() ?? "Unknown"}");
                if (File.Exists(localFilePath)) TryDeleteFile(localFilePath);
                return false;
            }

            long actualLength = contentResponse.Content.Headers.ContentLength ?? -1;
            if (actualLength >= 0 && actualLength != totalFileSize)
            {
                Console.WriteLine($"[WARN] Content-Length in download response ({actualLength}) differs from HEAD request ({totalFileSize}). Using response length.");
                totalFileSize = actualLength;
                _speedEstimator.Start(totalFileSize, 0);
                ReportProgress(onProgressChanged, totalFileSize, 0);
            }

            Console.WriteLine($"[DEBUG] Writing {totalFileSize} bytes to file: {localFilePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);

            using (var contentStream = await contentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[_options.BufferSize];
                int bytesRead;
                long bytesDownloadedInThisAttempt = 0;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                    bytesDownloadedInThisAttempt += bytesRead;
                    ReportProgress(onProgressChanged, totalFileSize, bytesDownloadedInThisAttempt);
                }
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            Console.WriteLine($"[INFO] Full download stream copy complete for {localFilePath}");
            return true; // Success
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[ERROR] File system error during full download to {localFilePath}: {ex.Message}");
            fileStream?.Dispose(); // Dispose stream
            fileStream = null;
            await Task.Delay(100); // Small delay before deleting
            TryDeleteFile(localFilePath);
            return false;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[WARN] Cancellation occurred during full download stream copy for {localFilePath}.");
            fileStream?.Dispose(); // Dispose stream
            fileStream = null;
            await Task.Delay(100); // Small delay before deleting
            TryDeleteFile(localFilePath); // Attempt cleanup
            throw; // Re-throw OCE
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[WARN] Timeout occurred during full download for {contentFileUrl}.");
            fileStream?.Dispose(); // Dispose stream
            fileStream = null;
            await Task.Delay(100); // Small delay before deleting
            TryDeleteFile(localFilePath); // Attempt cleanup
            throw; // Re-throw TimeoutException
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unexpected error during full download for {contentFileUrl}: {ex.Message}");
            fileStream?.Dispose(); // Dispose stream
            fileStream = null;
            await Task.Delay(100); // Small delay before deleting
            TryDeleteFile(localFilePath);
            return false;
        }
        finally
        {
            fileStream?.Dispose(); // Ensure disposal if not done in catch
            contentResponse?.Dispose();
        }
    }

    // ... (Rest of the methods remain the same) ...
    private async Task<bool> PerformPartialDownloadAsync(string contentFileUrl, string localFilePath, long totalFileSize,
    long initialBytesDownloaded, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[INFO] Attempting partial download/resume for {contentFileUrl} to {localFilePath} starting from {initialBytesDownloaded}");
        long currentTotalBytesDownloaded = initialBytesDownloaded;
        FileStream? fileStream = null;
        try
        {
            // Ensure directory exists before opening/creating the file stream
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            // Use FileMode.Create if starting from 0 (restart), otherwise OpenOrCreate (though Create would also work)
            FileMode fileOpenMode = (initialBytesDownloaded == 0) ? FileMode.Create : FileMode.OpenOrCreate;
            fileStream = new FileStream(localFilePath, fileOpenMode, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);

            // Seek only if resuming (initialBytes > 0). If restarting (initialBytes = 0), Create mode handles starting fresh.
            if (initialBytesDownloaded > 0)
            {
                fileStream.Seek(initialBytesDownloaded, SeekOrigin.Begin);
                Console.WriteLine($"[DEBUG] Seeked to {initialBytesDownloaded} in {localFilePath} for resume.");
            }
            else if (fileOpenMode == FileMode.Create)
            {
                Console.WriteLine($"[DEBUG] Opened {localFilePath} with FileMode.Create for restart from byte 0.");
            }


            while (currentTotalBytesDownloaded < totalFileSize)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check before each chunk attempt

                long bytesRemaining = totalFileSize - currentTotalBytesDownloaded;
                long bytesToDownloadThisChunk = Math.Min(bytesRemaining, _options.ChunkSize);
                long rangeFrom = currentTotalBytesDownloaded;
                long rangeTo = rangeFrom + bytesToDownloadThisChunk - 1;

                // Prevent invalid range if calculation somehow results in from > to
                if (rangeFrom > rangeTo || rangeFrom >= totalFileSize) break;

                long bytesReadThisChunk = await DownloadChunkAsync(fileStream, contentFileUrl, rangeFrom, rangeTo, totalFileSize, currentTotalBytesDownloaded, onProgressChanged, cancellationToken).ConfigureAwait(false); // Can throw OCE/Timeout

                if (bytesReadThisChunk < 0)
                {
                    Console.WriteLine($"[WARN] Chunk download failed ({rangeFrom}-{rangeTo}). Aborting partial download, keeping existing file.");
                    return false; // Chunk failed after retries, keep partial file and report failure
                }
                else if (bytesReadThisChunk == 0 && currentTotalBytesDownloaded < totalFileSize)
                {
                    // If server returns 0 bytes (e.g., 416 Range Not Satisfiable treated as completion)
                    // but we haven't reached total size, assume something is wrong or finished unexpectedly.
                    Console.WriteLine($"[WARN] Chunk download returned 0 bytes unexpectedly ({rangeFrom}-{rangeTo}). Assuming download complete, will verify.");
                    currentTotalBytesDownloaded = totalFileSize; // Force loop to exit, verification will catch issues
                    break;
                }

                currentTotalBytesDownloaded += bytesReadThisChunk;
                Console.WriteLine($"[TRACE] Chunk {rangeFrom}-{rangeTo} downloaded ({bytesReadThisChunk} bytes). Total downloaded: {currentTotalBytesDownloaded}");

                // Optional sanity check: Ensure file stream position matches
                if (fileStream.Position != currentTotalBytesDownloaded)
                {
                    Console.WriteLine($"[WARN] File stream position {fileStream.Position} does not match expected bytes downloaded {currentTotalBytesDownloaded}. Attempting to seek.");
                    try
                    {
                        fileStream.Seek(currentTotalBytesDownloaded, SeekOrigin.Begin);
                    }
                    catch (IOException ioEx)
                    {
                        Console.WriteLine($"[ERROR] Failed to seek file stream after chunk download: {ioEx.Message}. Aborting.");
                        return false;
                    }
                }
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false); // Can throw OCE
            Console.WriteLine($"[INFO] Partial download stream copy loop finished for {localFilePath}. Total bytes potentially written: {currentTotalBytesDownloaded}");

            // Verify final size after loop
            fileStream.Dispose(); // Ensure file handle is released before checking size
            fileStream = null; // Nullify to prevent dispose in finally block

            // Add small delay before checking file size, sometimes filesystem needs a moment
            await Task.Delay(50, CancellationToken.None); // Use CancellationToken.None as this delay shouldn't be cancelled by the main token

            long finalSize = new FileInfo(localFilePath).Length;
            if (finalSize < totalFileSize)
            {
                Console.WriteLine($"[WARN] Partial download finished, but final file size ({finalSize}) is less than total server size ({totalFileSize}).");
                return false; // Indicate failure if incomplete
            }

            Console.WriteLine($"[INFO] Partial download appears complete based on file size ({finalSize} >= {totalFileSize}). Proceeding to verification.");
            return true; // Completed successfully
        }
        catch (IOException ex) { Console.WriteLine($"[ERROR] File system error during partial download to {localFilePath}: {ex.Message}"); return false; } // Keep partial file
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Cancellation occurred during partial download setup or loop for {localFilePath}. Keeping partial file."); throw; } // Re-throw OCE
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout occurred during partial download for {contentFileUrl}. Keeping partial file."); throw; } // Re-throw Timeout
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during partial download for {contentFileUrl}: {ex.Message}"); return false; } // Keep partial file
        finally { fileStream?.Dispose(); } // Dispose if not already disposed
    }


    private async Task<long> DownloadChunkAsync(FileStream fileStream, string url, long rangeFrom, long rangeTo, long totalFileSize, long currentTotalBeforeChunk, Action<FileProgress> onProgressChanged, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Requesting chunk {rangeFrom}-{rangeTo} for {url}");
        HttpResponseMessage? partialResponse = null;
        try
        {
            partialResponse = await ExecuteWithRetryAsync(
                (token) => _webSystemCalls.DownloadPartialContentAsync(url, rangeFrom, rangeTo, token),
                $"DownloadPartialContent ({rangeFrom}-{rangeTo} - {url})",
                cancellationToken).ConfigureAwait(false); // Can throw OCE/TimeoutException

            // Handle failure after retries
            if (partialResponse == null || (!partialResponse.IsSuccessStatusCode && partialResponse.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                Console.WriteLine($"[ERROR] Failed to download chunk {rangeFrom}-{rangeTo} for {url} after retries. Status: {partialResponse?.StatusCode.ToString() ?? "Unknown"}");
                return -1; // Indicate chunk failure
            }

            // Handle "Range Not Satisfiable" - implies we might have asked for something beyond the file end, treat as 0 bytes successfully read for this chunk.
            // This can happen if the file size changed or if our total size calculation was slightly off.
            if (partialResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Console.WriteLine($"[WARN] Received 416 Requested Range Not Satisfiable for chunk {rangeFrom}-{rangeTo}. Assuming end of file reached or range invalid.");
                return 0; // Treat as 0 bytes downloaded for this chunk request
            }

            // Handle success (PartialContent or OK if server ignores range and sends whole file - unlikely but handle defensively)
            if (partialResponse.IsSuccessStatusCode) // Includes 200 OK and 206 Partial Content
            {
                if (partialResponse.StatusCode == HttpStatusCode.OK)
                {
                    Console.WriteLine($"[WARN] Received 200 OK instead of 206 Partial Content for chunk {rangeFrom}-{rangeTo}. Server might not fully support ranges. Handling response stream.");
                    // This scenario is tricky. If the server sends the *whole file* starting from byte 0,
                    // we should ideally detect this and switch to a full download logic, or carefully handle overwriting.
                    // For simplicity here, we'll process the stream, but this could lead to inefficiency or incorrect resumes later.
                    // A robust solution might require more state management or restarting as full download if 200 is received unexpectedly.
                }
                else // Assuming 206 Partial Content
                {
                    Console.WriteLine($"[TRACE] Received 206 Partial Content for chunk {rangeFrom}-{rangeTo}. Writing to stream.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Ensure stream is positioned correctly before writing this chunk's data
                if (fileStream.Position != rangeFrom)
                {
                    Console.WriteLine($"[DEBUG] Stream position is {fileStream.Position}, seeking to {rangeFrom} before writing chunk.");
                    fileStream.Seek(rangeFrom, SeekOrigin.Begin); // Can throw IO
                }

                using (var partialContentStream = await partialResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                { // Can throw OCE
                    var buffer = new byte[_options.BufferSize];
                    int bytesRead;
                    long bytesReadThisChunk = 0;
                    while ((bytesRead = await partialContentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    { // Can throw OCE
                        cancellationToken.ThrowIfCancellationRequested();
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false); // Can throw OCE, IO
                        bytesReadThisChunk += bytesRead;
                        // Report progress based on the total downloaded *before* this chunk + bytes read *so far* in this chunk
                        ReportProgress(onProgressChanged, totalFileSize, currentTotalBeforeChunk + bytesReadThisChunk);
                    }
                    Console.WriteLine($"[TRACE] Finished writing {bytesReadThisChunk} bytes for chunk {rangeFrom}-{rangeTo}.");
                    return bytesReadThisChunk; // Return actual bytes read in this chunk
                }
            }
            else // Handle other unexpected non-success status codes
            {
                Console.WriteLine($"[ERROR] Received unexpected status code {partialResponse.StatusCode} for chunk {rangeFrom}-{rangeTo}.");
                return -1; // Indicate chunk failure
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Operation cancelled during processing of chunk {rangeFrom}-{rangeTo}."); throw; } // Re-throw OCE
        catch (TimeoutException) { Console.WriteLine($"[WARN] Timeout occurred downloading chunk {rangeFrom}-{rangeTo}."); throw; } // Re-throw Timeout
        catch (IOException ex) { Console.WriteLine($"[ERROR] IO error processing chunk {rangeFrom}-{rangeTo}: {ex.Message}"); return -1; } // Return -1 on IO
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error downloading chunk {rangeFrom}-{rangeTo}: {ex.Message}"); return -1; } // Return -1 on unexpected
        finally { partialResponse?.Dispose(); }
    }

    private void ReportProgress(Action<FileProgress> onProgressChanged, long totalFileSize, long totalBytesDownloaded)
    {
        // Ensure bytes downloaded doesn't exceed total size for reporting
        totalBytesDownloaded = Math.Min(totalBytesDownloaded, totalFileSize);

        _speedEstimator.UpdateBytesDownloaded(totalBytesDownloaded);

        double? progressPercent = null;
        if (totalFileSize > 0)
        {
            progressPercent = Math.Max(0.0, Math.Min(100.0, (double)totalBytesDownloaded / totalFileSize * 100.0));
        }
        else if (totalBytesDownloaded == 0) // If total size is 0 (or unknown negative), report 0% initially
        {
            progressPercent = 0.0;
        }
        // If totalFileSize <= 0 and totalBytesDownloaded > 0, percentage remains null (unknown)


        TimeSpan? estimatedRemaining = _speedEstimator.EstimateRemainingTime();

        try
        {
            onProgressChanged?.Invoke(new FileProgress(totalFileSize, totalBytesDownloaded, progressPercent, estimatedRemaining));
        }
        catch (Exception ex)
        {
            // Avoid crashing the download due to a faulty progress callback
            Console.WriteLine($"[WARN] Error occurred within the onProgressChanged callback: {ex.Message}");
        }
    }

    private async Task<bool> VerifyIntegrityAsync(string localFilePath, byte[]? expectedMd5Hash, CancellationToken cancellationToken, bool logSkip = false)
    {
        // This method correctly re-throws OCE
        if (expectedMd5Hash == null)
        {
            if (!logSkip) Console.WriteLine($"[INFO] MD5 header not provided. Skipping integrity check for {localFilePath}.");
            return true;
        }

        // Check file exists *before* trying to open it
        if (!File.Exists(localFilePath))
        {
            Console.WriteLine($"[ERROR] Cannot verify integrity: File {localFilePath} does not exist.");
            return false;
        }


        try
        {
            cancellationToken.ThrowIfCancellationRequested(); // Check before hash calculation
            if (!logSkip) Console.WriteLine($"[INFO] Calculating MD5 hash for {localFilePath}");

            byte[] calculatedHash;
            // Use a smaller buffer for hashing if BufferSize is very large, as reading huge chunks might not be optimal for hashing IO
            int hashBufferSize = Math.Min(_options.BufferSize, 1 * 1024 * 1024); // e.g., max 1MB buffer for hashing

            using (var md5 = MD5.Create())
            // Ensure file can be opened for reading, handle potential exceptions here
            using (var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, hashBufferSize, FileOptions.Asynchronous))
            {
                calculatedHash = await md5.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false); // Pass CT here
            }


            cancellationToken.ThrowIfCancellationRequested(); // Check after hash calculation

            if (expectedMd5Hash.SequenceEqual(calculatedHash))
            {
                if (!logSkip) Console.WriteLine($"[INFO] Integrity check passed for {localFilePath}: MD5 hashes match.");
                return true;
            }
            else
            {
                if (!logSkip)
                {
                    Console.WriteLine($"[ERROR] Integrity check failed for {localFilePath}: MD5 hashes do not match!");
                    Console.WriteLine($"  Expected:   {BitConverter.ToString(expectedMd5Hash).Replace("-", "")}");
                    Console.WriteLine($"  Calculated: {BitConverter.ToString(calculatedHash).Replace("-", "")}");
                    TryDeleteFile(localFilePath); // Delete the corrupted file
                }
                return false;
            }
        }
        // Catch specific file access exceptions during FileStream creation or reading
        catch (IOException ex) { Console.WriteLine($"[ERROR] IO Error reading file {localFilePath} for integrity check: {ex.Message}"); return false; }
        catch (UnauthorizedAccessException ex) { Console.WriteLine($"[ERROR] Permission denied reading file {localFilePath} for integrity check: {ex.Message}"); return false; }
        catch (OperationCanceledException) { Console.WriteLine($"[WARN] Operation cancelled during integrity check for {localFilePath}."); throw; } // Re-throw
        catch (Exception ex) { Console.WriteLine($"[ERROR] Unexpected error during integrity check for {localFilePath}: {ex.Message}"); return false; }
    }


    private async Task<HttpResponseMessage?> ExecuteWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> action,
            string operationName,
            CancellationToken cancellationToken)
    {
        // This method correctly re-throws OCE/TimeoutException
        TimeSpan currentDelay = _options.InitialRetryDelay;
        HttpResponseMessage? response = null;

        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested(); // Check before attempt

            try
            {
                // Delay before retrying (but not on the first attempt)
                if (attempt > 0)
                {
                    Console.WriteLine($"[DEBUG] Retrying {operationName} in {currentDelay.TotalSeconds:F1}s (Attempt {attempt}/{_options.MaxRetries})...");
                    await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false); // Respect cancellation during delay
                    // Exponential backoff, capped at MaxRetryDelay
                    currentDelay = TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, _options.MaxRetryDelay.TotalSeconds));
                }

                Console.WriteLine($"[TRACE] Executing {operationName}, attempt {attempt}");
                response = await action(cancellationToken).ConfigureAwait(false); // Pass CT to the action

                // Success conditions: OK, PartialContent, or RangeNotSatisfiable (treated as success for chunking)
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Console.WriteLine($"[TRACE] {operationName} succeeded on attempt {attempt} with status {response.StatusCode}.");
                    return response;
                }

                // Determine if the status code warrants a retry
                bool shouldRetry = response.StatusCode >= HttpStatusCode.InternalServerError || // 5xx errors
                                   response.StatusCode == HttpStatusCode.RequestTimeout ||       // 408
                                   response.StatusCode == HttpStatusCode.TooManyRequests;       // 429

                if (!shouldRetry)
                {
                    Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} with non-retriable status code {response.StatusCode}. Won't retry.");
                    return response; // Return the failure response immediately
                }

                Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} with retriable status code {response.StatusCode}.");

                // Dispose response before retrying only if it's not the last attempt
                if (attempt < _options.MaxRetries)
                {
                    response.Dispose();
                    response = null;
                }
                // Otherwise, keep the last response to return it after loop finishes
            }
            catch (HttpRequestException ex) // Network-level errors (DNS, connection refused, etc.)
            {
                response?.Dispose(); // Ensure response is disposed if created before exception
                response = null;
                Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} with network error: {ex.Message}");
                if (attempt >= _options.MaxRetries) throw; // Rethrow if max retries reached
                                                           // Backoff logic is handled by the delay at the start of the next loop iteration
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested) // Handles HttpClient timeouts
            {
                response?.Dispose();
                response = null;
                Console.WriteLine($"[WARN] {operationName} failed on attempt {attempt} due to a timeout: {ex.InnerException?.Message ?? ex.Message}");
                if (attempt >= _options.MaxRetries) throw new TimeoutException($"Operation '{operationName}' timed out after {_options.MaxRetries + 1} attempts.", ex); // Rethrow specific TimeoutException
                                                                                                                                                                        // Backoff logic is handled by the delay at the start of the next loop iteration
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested) // Handles explicit cancellation
            {
                response?.Dispose();
                Console.WriteLine($"[WARN] Operation cancelled by token during {operationName} on attempt {attempt}.");
                throw; // Re-throw immediately on cancellation
            }
            catch (Exception ex) // Catch unexpected errors during the action
            {
                response?.Dispose();
                response = null;
                Console.WriteLine($"[ERROR] Unexpected error during {operationName} on attempt {attempt}: {ex.GetType().Name} - {ex.Message}");
                if (attempt >= _options.MaxRetries) throw; // Rethrow if max retries reached
                                                           // Backoff logic is handled by the delay at the start of the next loop iteration
            }
        }

        // If loop finishes (max retries reached without success or non-retriable failure), return the last response (which will be null if an exception occurred on the last attempt, or the failing response object otherwise)
        Console.WriteLine($"[ERROR] {operationName} failed after {_options.MaxRetries + 1} attempts.");
        return response;
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Console.WriteLine($"[INFO] Attempting to delete file: {filePath}");
                File.Delete(filePath);
                Console.WriteLine($"[DEBUG] Successfully deleted file: {filePath}");
            }
        }
        catch (IOException ex)
        {
            // Log failure to delete but don't crash
            Console.WriteLine($"[WARN] Could not delete file '{filePath}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[WARN] No permission to delete file '{filePath}': {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unexpected error deleting file '{filePath}': {ex.Message}");
        }
    }
}