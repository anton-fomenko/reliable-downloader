using ReliableDownloader;

var exampleUrl = args.Length > 0
    ? args[0]
    // If this url 404's, you can get a live one from https://installer.demo.accurx.com/chain/latest.json.
    : "https://installer.demo.accurx.com/chain/4.22.50587.0/accuRx.Installer.Local.msi";

var exampleFilePath = args.Length > 1
    ? args[1]
    : Path.Combine(Directory.GetCurrentDirectory(), "myfirstdownload.msi");

using var cts = new CancellationTokenSource();

// Optional: Set a timeout based on the third argument (milliseconds)
if (args.Length > 2 && int.TryParse(args[2], out int milliseconds))
{
    Console.WriteLine($"[INFO] Setting cancellation timeout: {milliseconds}ms");
    cts.CancelAfter(milliseconds);
}

// Setup progress reporting and downloader
var progressReporter = new ConsoleProgressReporter();
var fileDownloader = new FileDownloader(new WebSystemCalls()); // Uses default options

Console.WriteLine($"[INFO] Attempting download:");
Console.WriteLine($"  URL: {exampleUrl}");
Console.WriteLine($"  Output: {exampleFilePath}");

bool didDownloadSuccessfully = false;
try
{
    // Start the download
    didDownloadSuccessfully = await fileDownloader.TryDownloadFile(
        exampleUrl,
        exampleFilePath,
        progressReporter.HandleProgress, // Pass the reporter's method
        cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[WARN] Download cancelled.");
    didDownloadSuccessfully = false;
}
catch (Exception ex) // Catch unexpected errors
{
    Console.WriteLine($"[FAIL] An unexpected error occurred: {ex.Message}");
    didDownloadSuccessfully = false;
}

Console.WriteLine($"[INFO] Download {(didDownloadSuccessfully ? "succeeded" : "failed")}.");

// Return non-zero exit code on failure
return didDownloadSuccessfully ? 0 : 1;