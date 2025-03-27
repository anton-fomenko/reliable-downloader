namespace ReliableDownloader
{
    /// <summary>
    /// Configuration options for the FileDownloader.
    /// </summary>
    public class FileDownloaderOptions
    {
        /// <summary> Maximum number of retries for failed HTTP requests. </summary>
        public int MaxRetries { get; set; } = 15;

        /// <summary> Initial delay before the first retry. </summary>
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary> Maximum delay between retries (using exponential backoff). </summary>
        public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary> Preferred size for downloading chunks when partial downloads are supported. </summary>
        public long ChunkSize { get; set; } = 1 * 1024 * 1024; // 1 MiB

        /// <summary> Buffer size used for reading from network streams and writing to files. </summary>
        public int BufferSize { get; set; } = 81920; // Default from Stream.CopyToAsync
    }
}