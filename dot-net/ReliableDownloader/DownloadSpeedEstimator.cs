using System.Diagnostics;

namespace ReliableDownloader
{
    /// <summary>
    /// Helper to estimate download speed and remaining time.
    /// </summary>
    internal class DownloadSpeedEstimator
    { /* ... Same as before ... */
        private readonly Stopwatch _stopwatch = new();
        private long _startTimestampBytes = 0; private long _totalFileSize = 0; private long _totalBytesDownloaded = 0;
        public void Start(long totalFileSize, long initialBytesDownloaded) { _totalFileSize = totalFileSize; _totalBytesDownloaded = initialBytesDownloaded; _startTimestampBytes = initialBytesDownloaded; _stopwatch.Restart(); }
        public void UpdateBytesDownloaded(long totalBytesDownloaded) { _totalBytesDownloaded = totalBytesDownloaded; }
        public void Stop() { _stopwatch.Stop(); }
        public TimeSpan? EstimateRemainingTime() { if (!_stopwatch.IsRunning || _totalFileSize <= 0 || _totalBytesDownloaded <= _startTimestampBytes) return null; double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds; if (elapsedSeconds < 0.5) return null; long bytesSinceStart = _totalBytesDownloaded - _startTimestampBytes; if (bytesSinceStart <= 0) return null; double bytesPerSecond = bytesSinceStart / elapsedSeconds; if (bytesPerSecond < 1) return null; long remainingBytes = _totalFileSize - _totalBytesDownloaded; if (remainingBytes <= 0) return TimeSpan.Zero; try { double secondsRemaining = remainingBytes / bytesPerSecond; if (secondsRemaining > TimeSpan.MaxValue.TotalSeconds) return TimeSpan.MaxValue; return TimeSpan.FromSeconds(secondsRemaining); } catch (OverflowException) { return TimeSpan.MaxValue; } }
    }

}
