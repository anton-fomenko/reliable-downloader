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
            // Report initial progress
            ReportProgress(onProgressChanged, totalFileSize.Value, totalBytesDownloaded, TimeSpan.Zero); // TODO: Estimate time later

            if (!partialDownloadSupported)
            {
                Console.WriteLine("Partial download not supported. Attempting full download.");
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
                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        totalBytesDownloaded += bytesRead;

                        // Report progress during copy
                        ReportProgress(onProgressChanged, totalFileSize.Value, totalBytesDownloaded, TimeSpan.Zero); // TODO: Estimate time
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
}