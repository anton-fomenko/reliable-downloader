namespace ReliableDownloader
{
    /// <summary>
    /// Configuration options for the FileDownloader.
    /// </summary>
    public class FileDownloaderOptions
    { /* ... Same as before ... */
        public int MaxRetries { get; set; } = 15;
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);
        public long ChunkSize { get; set; } = 1 * 1024 * 1024;
        public int BufferSize { get; set; } = 81920;
    }
}
