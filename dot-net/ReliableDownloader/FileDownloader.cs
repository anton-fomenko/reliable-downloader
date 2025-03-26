namespace ReliableDownloader;

internal sealed class FileDownloader : IFileDownloader
{
    private readonly IWebSystemCalls _webSystemCalls;

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

            if (!totalFileSize.HasValue)
            {
                Console.WriteLine("Cannot determine file size from headers. Cannot proceed.");
                // Or potentially attempt a full download if size is not strictly needed upfront
                return false;
            }


            // --- Placeholder for next steps ---
            Console.WriteLine("Header check successful. Download logic to be implemented.");


            // For now, just return false as download isn't implemented
            return false;

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
}