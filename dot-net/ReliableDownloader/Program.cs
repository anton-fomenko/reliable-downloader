using ReliableDownloader;

var exampleUrl = args.Length > 0
    ? args[0]
    // If this url 404's, you can get a live one from https://installer.demo.accurx.com/chain/latest.json.
    : "https://installer.demo.accurx.com/chain/4.22.50587.0/accuRx.Installer.Local.msi";

var exampleFilePath = args.Length > 1
    ? args[1]
    : Path.Combine(Directory.GetCurrentDirectory(), "myfirstdownload.msi");

using var cts = new CancellationTokenSource();

if (args.Length > 2)
{
    cts.CancelAfter(TimeSpan.FromMilliseconds(int.Parse(args[2])));
}

var fileDownloader = new FileDownloader(new WebSystemCalls());

int lastReportedPercentage = -1; // Track the last reported whole percentage

var didDownloadSuccessfully = await fileDownloader.TryDownloadFile(
    exampleUrl,
    exampleFilePath,
    progress =>
    {
        if (progress.ProgressPercent.HasValue && progress.ProgressPercent.Value >= 0)
        {
            int currentPercentage = (int)Math.Floor(progress.ProgressPercent.Value);
            // Only print if it's a new whole percentage point (or the first report)
            if (currentPercentage > lastReportedPercentage)
            {
                Console.WriteLine($"[PROGRESS] {currentPercentage}% complete. Estimated time remaining: {progress.EstimatedRemaining?.ToString(@"hh\:mm\:ss") ?? "Calculating..."}");
                lastReportedPercentage = currentPercentage;
            }
            // Ensure 100% completion is always reported
            else if (progress.ProgressPercent.Value == 100 && lastReportedPercentage != 100)
            {
                Console.WriteLine($"[PROGRESS] {progress.ProgressPercent.Value}% complete. Download finished.");
                lastReportedPercentage = 100;
            }
        }
        // Handle case where percentage might not be calculated yet
        else if (lastReportedPercentage == -1) // Only report initial state once
        {
            Console.WriteLine($"[PROGRESS] Starting download (Size: {progress.TotalFileSize ?? 0} bytes)...");
            lastReportedPercentage = 0; // Mark initial message as sent
        }
    },
    cts.Token);

Console.WriteLine($"File download ended! Success: {didDownloadSuccessfully}");