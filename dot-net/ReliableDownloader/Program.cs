using ReliableDownloader;

// Use top-level statements feature of modern C#
var exampleUrl = args.Length > 0
    ? args[0]
    // If this url 404's, you can get a live one from https://installer.demo.accurx.com/chain/latest.json.
    : "https://installer.demo.accurx.com/chain/4.22.50587.0/accuRx.Installer.Local.msi"; // Example URL

var exampleFilePath = args.Length > 1
    ? args[1]
    : Path.Combine(Directory.GetCurrentDirectory(), "myfirstdownload.msi");

using var cts = new CancellationTokenSource();

// Optional: Set a timeout based on the third argument (in milliseconds)
if (args.Length > 2 && int.TryParse(args[2], out int milliseconds))
{
    Console.WriteLine($"[INFO] Setting cancellation timeout: {milliseconds}ms");
    cts.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
}

// *** Instantiate the custom progress reporter ***
var progressReporter = new ConsoleProgressReporter();

// Instantiate the downloader (consider making retry params configurable here if needed)
var fileDownloader = new FileDownloader(new WebSystemCalls()); // Using defaults or adjusted retry params

Console.WriteLine($"[INFO] Starting download attempt for: {exampleUrl}");
Console.WriteLine($"[INFO] Target file path: {exampleFilePath}");

bool didDownloadSuccessfully = false;
try
{
    // *** Pass the reporter's method as the action ***
    didDownloadSuccessfully = await fileDownloader.TryDownloadFile(
        exampleUrl,
        exampleFilePath,
        progressReporter.HandleProgress, // Use the instance method
        cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("[WARN] Download operation was cancelled.");
    didDownloadSuccessfully = false; // Ensure success is false if cancelled
}
catch (Exception ex) // Catch unexpected errors during the download call itself
{
    Console.WriteLine($"[FAIL] An unexpected error occurred: {ex.Message}");
    // Consider logging the full exception ex here using a proper logger
    didDownloadSuccessfully = false;
}

Console.WriteLine($"[INFO] File download ended! Success: {didDownloadSuccessfully}");

// Optional: Keep console open to see output if running from explorer
// Console.WriteLine("Press any key to exit.");
// Console.ReadKey();

// Return non-zero exit code on failure for scripting
return didDownloadSuccessfully ? 0 : 1;