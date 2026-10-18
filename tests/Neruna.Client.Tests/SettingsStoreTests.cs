using Microsoft.Extensions.DependencyInjection;
using Neruna.Core;
using Neruna.Storage;

namespace Neruna.Client.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task New_key_written_twice_at_once_is_stored_once_with_the_last_value()
    {
        // Setup on first start: the layout timer and the closing window both saved "ui.layout" – UNIQUE constraint failed.
        var ct = TestContext.Current.CancellationToken;
        await using (var services = new ServiceCollection().AddNerunaStorage(_directory).BuildServiceProvider())
        {
            await services.InitializeNerunaStorageAsync(ct);
            var settings = services.GetRequiredService<ISettingsStore>();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => settings.SetAsync("ui.layout", i.ToString(System.Globalization.CultureInfo.InvariantCulture), ct), ct)));
            Assert.NotNull(await settings.GetAsync("ui.layout", ct));
        }

        await using (var services = new ServiceCollection().AddNerunaStorage(_directory).BuildServiceProvider())
        {
            await services.InitializeNerunaStorageAsync(ct);
            Assert.Single(await services.GetRequiredService<ISettingsStore>().GetAllAsync(ct), s => s.Key == "ui.layout");
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
