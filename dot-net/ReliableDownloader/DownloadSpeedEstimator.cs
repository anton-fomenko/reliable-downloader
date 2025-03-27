using System.Diagnostics;

namespace ReliableDownloader
{
    /// <summary>
    /// Helper to estimate download speed and remaining time.
    /// </summary>
    internal class DownloadSpeedEstimator
    {
        private readonly Stopwatch _stopwatch = new();
        private long _startTimestampBytes = 0;
        private long _totalFileSize = 0;
        private long _totalBytesDownloaded = 0;

        /// <summary>
        /// Starts or restarts the estimation timer.
        /// </summary>
        /// <param name="totalFileSize">The total expected size of the file in bytes.</param>
        /// <param name="initialBytesDownloaded">The number of bytes already downloaded when starting.</param>
        public void Start(long totalFileSize, long initialBytesDownloaded)
        {
            _totalFileSize = totalFileSize;
            _totalBytesDownloaded = initialBytesDownloaded;
            _startTimestampBytes = initialBytesDownloaded; // Record bytes at the moment timing starts
            _stopwatch.Restart();
        }

        /// <summary>
        /// Updates the total number of bytes downloaded so far.
        /// </summary>
        /// <param name="totalBytesDownloaded">The current total bytes downloaded.</param>
        public void UpdateBytesDownloaded(long totalBytesDownloaded)
        {
            _totalBytesDownloaded = totalBytesDownloaded;
        }

        /// <summary>
        /// Stops the estimation timer.
        /// </summary>
        public void Stop()
        {
            _stopwatch.Stop();
        }

        /// <summary>
        /// Estimates the remaining download time based on progress since Start was called.
        /// </summary>
        /// <returns>An estimated TimeSpan, TimeSpan.Zero if complete, TimeSpan.MaxValue if potentially infinite, or null if estimation is not possible.</returns>
        public TimeSpan? EstimateRemainingTime()
        {
            if (!_stopwatch.IsRunning || _totalFileSize <= 0) return null; // Can't estimate if stopped or no total size

            double elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;

            // Need some time elapsed and progress made to make a reasonable estimate
            if (elapsedSeconds < 0.5) return null; // Avoid estimates based on very short intervals

            long bytesDownloadedSinceStart = _totalBytesDownloaded - _startTimestampBytes;

            // If no bytes downloaded since start, cannot estimate speed
            if (bytesDownloadedSinceStart <= 0) return null;

            double bytesPerSecond = bytesDownloadedSinceStart / elapsedSeconds;

            // Avoid division by zero or extremely low speeds causing huge estimates
            if (bytesPerSecond < 1) return null; // Effectively stalled or too slow to estimate reasonably

            long remainingBytes = _totalFileSize - _totalBytesDownloaded;

            if (remainingBytes <= 0) return TimeSpan.Zero; // Download is complete or somehow exceeded total size

            try
            {
                double secondsRemaining = remainingBytes / bytesPerSecond;

                // Check for potential overflow before converting to TimeSpan
                if (secondsRemaining > TimeSpan.MaxValue.TotalSeconds)
                {
                    return TimeSpan.MaxValue; // Indicate a very long time (effectively infinite)
                }
                // Check for negative results which shouldn't happen with checks above, but be safe
                if (secondsRemaining < 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds(secondsRemaining);
            }
            catch (OverflowException)
            {
                // Handle potential overflow during calculation (less likely with double but possible)
                return TimeSpan.MaxValue;
            }
        }
    }
}