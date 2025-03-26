namespace ReliableDownloader;

internal record FileProgress(
    long? TotalFileSize,
    long TotalBytesDownloaded,
    double? ProgressPercent,
    TimeSpan? EstimatedRemaining);
