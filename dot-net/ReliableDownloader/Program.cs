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

var didDownloadSuccessfully = await fileDownloader.TryDownloadFile(
    exampleUrl,
    exampleFilePath,
    progress => Console.WriteLine($"Percent progress is {progress.ProgressPercent}"),
    cts.Token);

Console.WriteLine($"File download ended! Success: {didDownloadSuccessfully}");