using Moq;
using NUnit.Framework;
using System.Net;

namespace ReliableDownloader.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class Tests
{
    private readonly FileDownloader _sut;
    private readonly Mock<IWebSystemCalls> _mockWebCalls = new();
    private FileProgress? _lastProgress;
    private CancellationTokenSource _cts;

    public Tests()
    {
        _sut = new FileDownloader(_mockWebCalls.Object);
        _cts = new CancellationTokenSource();
    }

    [TearDown]
    public void Teardown()
    {
        _cts.Dispose();
    }

    [Test]
    public async Task TryDownloadFile_ShouldReturnFalse_WhenHeadersRequestFails()
    {
        // Arrange
        var url = "http://fail.com/file.msi";
        var filePath = "file.msi";
        var response = new HttpResponseMessage(HttpStatusCode.NotFound); // Simulate failure

        _mockWebCalls.Setup(w => w.GetHeadersAsync(url, _cts.Token))
                     .ReturnsAsync(response);

        // Act
        var result = await _sut.TryDownloadFile(url, filePath, HandleProgress, _cts.Token);

        // Assert
        Assert.That(result == false);
        _mockWebCalls.Verify(w => w.GetHeadersAsync(url, _cts.Token), Times.Once); // Verify HEAD call was made
    }

    private void HandleProgress(FileProgress progress)
    {
        _lastProgress = progress;
        Console.WriteLine($"Progress: {progress}");
    }
}