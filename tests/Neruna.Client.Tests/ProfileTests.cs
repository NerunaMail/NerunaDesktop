using Neruna.Storage;
using Neruna.Storage.Credentials;

namespace Neruna.Client.Tests;

/// <summary>Profiles: Neruna side by side with own data, each in its own window.</summary>
public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "neruna-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Without_a_list_the_data_folder_is_the_standard_profile()
    {
        var list = ProfileList.Load(_root);

        var profile = Assert.Single(list.All);
        Assert.True(profile.IsStandard);
        Assert.Same(profile, list.Resolve([]));
        Assert.Equal(_root, list.DirectoryOf(profile));
    }

    [Fact]
    public void Profiles_are_found_by_name_or_id_and_the_default_opens_without_an_argument()
    {
        var list = ProfileList.Load(_root);
        var business = list.Add(" Business ");
        Assert.True(Directory.Exists(list.DirectoryOf(business)));
        Assert.StartsWith(Path.Combine(_root, "profiles"), list.DirectoryOf(business), StringComparison.Ordinal);

        var reloaded = ProfileList.Load(_root);
        Assert.Equal("Business", reloaded.Resolve(["--profile", "business"])?.Name);
        Assert.Equal("Business", reloaded.Resolve([$"--profile={business.Id}"])?.Name);
        Assert.Null(reloaded.Resolve(["--profile", "Verein"]));
        Assert.True(reloaded.Resolve([])!.IsStandard);

        reloaded.SetDefault(business.Id);
        Assert.Equal(business.Id, ProfileList.Load(_root).Resolve(["mailto:lea@example.com"])!.Id);
        Assert.Equal(["mailto:lea@example.com"], ProfileList.WithoutProfile(["--profile", "Business", "mailto:lea@example.com"]));

        Assert.Throws<ArgumentException>(() => reloaded.Add("BUSINESS"));
        reloaded.Rename(Guid.Empty, "Privat");
        Assert.Equal("Privat", ProfileList.Load(_root).All[0].Name);
    }

    [Fact]
    public void A_changed_list_cannot_point_outside_the_data_folder()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ProfileList.FileName),
            """{"profiles":[{"id":"6f1c7a52-8d1e-4c11-9f0a-1b2c3d4e5f60","name":"Fremd","folder":"../elsewhere"},{"id":"6f1c7a52-8d1e-4c11-9f0a-1b2c3d4e5f61","name":"Absolut","folder":"/tmp/x"}]}""");

        Assert.Single(ProfileList.Load(_root).All);
    }

    [Fact]
    public async Task Each_profile_keeps_its_secrets_under_ids_of_its_own_and_takes_them_along_when_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var list = ProfileList.Load(_root);
        var business = list.Add("Business");
        var privat = list.Add("Privat");
        using var system = new LocalCredentialStore(Path.Combine(_root, "system"));
        var cloudKey = new Guid("6e657275-6e61-436c-6f75-640000000001");
        var inBusiness = new ProfileCredentialStore(system, business.Id, list.DirectoryOf(business));
        var inPrivat = new ProfileCredentialStore(system, privat.Id, list.DirectoryOf(privat));

        await system.SetSecretAsync(cloudKey, "standard", ct);
        await inBusiness.SetSecretAsync(cloudKey, "business", ct);
        await inPrivat.SetSecretAsync(cloudKey, "privat", ct);

        // The same fixed entry, three values: none overwrites another; the standard profile's id is unchanged.
        Assert.Equal(("standard", "business", "privat"),
            (await system.GetSecretAsync(cloudKey, ct), await inBusiness.GetSecretAsync(cloudKey, ct), await inPrivat.GetSecretAsync(cloudKey, ct)));

        await list.RemoveAsync(business.Id, profile => ProfileCredentialStore.DeleteAllAsync(system, list.DirectoryOf(profile), ct));
        Assert.Null(await system.GetSecretAsync(ProfileCredentialStore.Scoped(business.Id, cloudKey), ct));
        Assert.False(Directory.Exists(Path.Combine(_root, business.Folder)));
        Assert.Equal("privat", await inPrivat.GetSecretAsync(cloudKey, ct));
        Assert.Equal(["Standard", "Privat"], ProfileList.Load(_root).All.Select(p => p.Name));
    }
}
