namespace Neruna.Shared.Tests;

/// <summary>
/// Example documents of the API contract shared with the server (private repository NerunaAPI, checked out as
/// `../api` next to this repository). Without it – e.g. in a plain clone of the public repository – the tests that
/// need them are skipped instead of failing.
/// </summary>
internal static class ContractFixtures
{
    public static string Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Assert.SkipUnless(File.Exists(path), "API-Vertrag nicht vorhanden (privates Repo NerunaAPI als ../api auschecken)");
        return File.ReadAllText(path);
    }
}
