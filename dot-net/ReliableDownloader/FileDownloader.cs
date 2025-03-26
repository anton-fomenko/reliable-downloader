namespace ReliableDownloader;

internal sealed class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;
    private const int BufferSize = 81920; // 80 KB buffer for copying streams

    public FileDownloader(IWebSystemCalls webSystemCalls)
    {
        _webSystemCalls = webSystemCalls;
    }

    public async Task<bool> TryDownloadFile(
        string contentFileUrl,
        string localFilePath,
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            // --- Step 1: Get Headers ---
            using var headersResponse = await _webSystemCalls
                .GetHeadersAsync(contentFileUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!headersResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to get headers. Status code: {headersResponse.StatusCode}");
                // In a real implementation, we'd likely retry here based on the status code
                return false;
            }

            // --- Step 2: Check Capabilities ---
            bool partialDownloadSupported = headersResponse.Headers.AcceptRanges.Contains("bytes");
            long? totalFileSize = headersResponse.Content.Headers.ContentLength;
            byte[]? md5Hash = headersResponse.Content.Headers.ContentMD5; // Note: ContentMD5 is often null/not sent

            Console.WriteLine($"Partial download supported: {partialDownloadSupported}");
            Console.WriteLine($"Total file size: {totalFileSize?.ToString() ?? "Unknown"}");
            Console.WriteLine($"MD5 Hash available: {md5Hash != null}");

            if (!totalFileSize.HasValue || totalFileSize == 0)
            {
                Console.WriteLine("Cannot determine file size from headers. Cannot proceed.");
                // Or potentially attempt a full download if size is not strictly needed upfront
                return false;
            }

            long totalBytesDownloaded = 0;

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
                            ReportProgress(onProgressChanged, totalFileSize.Value, totalBytesDownloaded, TimeSpan.Zero);
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
            ReportProgress(onProgressChanged, totalFileSize.Value, totalBytesDownloaded, TimeSpan.Zero); // TODO: Estimate time later

            if (!partialDownloadSupported)
            {
                // ... (ensure totalBytesDownloaded is 0 if full download needed) ...
                if (totalBytesDownloaded > 0)
                {
                    Console.WriteLine("Partial download not supported by server, but local file exists. Deleting local file and starting full download.");
                    TryDeleteFile(localFilePath);
                    totalBytesDownloaded = 0;
                    ReportProgress(onProgressChanged, totalFileSize.Value, totalBytesDownloaded, TimeSpan.Zero); // Reset progress
                }

                Console.WriteLine("Partial download not supported. Attempting full download.");
                try
                {
                    return await PerformFullDownloadAsync(contentFileUrl, localFilePath, totalFileSize.Value, 
                        onProgressChanged, cancellationToken);

                }
                catch (Exception ex)
                { 
                    Console.WriteLine($"Error during full download path: {ex.Message}");
                    TryDeleteFile(localFilePath);
                    return false;
                }
            }
            else
            {
                // --- Placeholder for Partial Download Logic (Next Step) ---
                Console.WriteLine("Partial download. Logic to be implemented.");
                return false; // Not implemented yet
            }
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

    private async Task<bool> PerformFullDownloadAsync(
        string contentFileUrl, 
        string localFilePath, 
        long totalFileSize, 
        Action<FileProgress> onProgressChanged,
        CancellationToken cancellationToken)
    {
        try
        {
            using var contentResponse = await _webSystemCalls.DownloadContentAsync(contentFileUrl, cancellationToken).ConfigureAwait(false);

            if (!contentResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to download content. Status code: {contentResponse.StatusCode}");
                return false; // Add retry later
            }

            Console.WriteLine($"Writing to file: {localFilePath}");
            using var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            using var contentStream = await contentResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[BufferSize];
            int bytesRead;
            long totalBytesDownloaded = 0;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalBytesDownloaded += bytesRead;

                // Report progress during copy
                ReportProgress(onProgressChanged, totalFileSize, totalBytesDownloaded, TimeSpan.Zero); // TODO: Estimate time
            }

            Console.WriteLine("Full download completed and file written.");
            // TODO: Add integrity check here in a later step
            return true; // Success for now

        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Network error during full download: {ex.Message}");
            return false; // Add retry later
        }
        catch (IOException ex)
        {
            Console.WriteLine($"File system error during full download: {ex.Message}");
            // Attempt to delete potentially corrupted file
            TryDeleteFile(localFilePath);
            return false;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation cancelled during full download.");
            // Attempt to delete potentially incomplete file
            TryDeleteFile(localFilePath);
            return false;
        }
    }
}