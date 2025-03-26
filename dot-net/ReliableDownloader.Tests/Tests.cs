using NUnit.Framework;

namespace ReliableDownloader.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class Tests
{
    private readonly FileDownloader _sut = new();

    [Test]
    public async Task Test1()
    {
        await _sut.TryDownloadFile("https://example.com/example.msi", "example.msi", _ => { }, default);

        Assert.Inconclusive("TODO");
    }
}