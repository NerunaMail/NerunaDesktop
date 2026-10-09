using System.Text;
using System.Text.Json;
using Neruna.Vault;

namespace Neruna.Shared.Tests;

public class UnlockedVaultTests
{
    // Cheap parameters keep the suite fast; production uses KdfParameters.Default.
    private static readonly KdfParameters FastKdf = new(1024, 1, 1);
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"accounts":[{"email":"anna@example.com","password":"geheim"}]}""");

    [Fact]
    public void Password_unlocks_and_opens_payload()
    {
        var envelope = CreateEnvelope("Correct horse", out _);

        using var vault = UnlockedVault.UnlockWithPassword(envelope, "Correct horse", FastKdf);

        Assert.Equal(Payload, vault.Open(envelope));
    }

    [Fact]
    public void Kept_data_key_resumes_the_vault_for_notes_backups_and_fingerprints()
    {
        using var created = UnlockedVault.Create("Correct horse", out _, FastKdf);
        var envelope = created.Seal(Payload);
        var note = created.EncryptText("Neues Firmenkonto");
        Assert.DoesNotContain("Firmenkonto", note, StringComparison.Ordinal);

        // What the device keeps in its keychain is enough – no password needed for the next backup.
        using var resumed = UnlockedVault.Resume(created.VaultId, created.ExportDataKey(), created.Keys);
        Assert.Equal(Payload, resumed.Open(envelope));
        Assert.Equal("Neues Firmenkonto", resumed.DecryptText(note));
        Assert.Equal(created.Fingerprint(Payload), resumed.Fingerprint(Payload));
        Assert.NotEqual(created.Fingerprint(Payload), resumed.Fingerprint("{}"u8));
        using (var reopened = UnlockedVault.UnlockWithPassword(resumed.Seal(Payload), "Correct horse", FastKdf))
        {
            Assert.Equal(Payload, reopened.Open(envelope));
        }

        // Another vault reads neither notes nor gets the same fingerprints; tampering is noticed.
        using var other = UnlockedVault.Create("Correct horse", out _, FastKdf);
        Assert.Null(other.DecryptText(note));
        Assert.NotEqual(created.Fingerprint(Payload), other.Fingerprint(Payload));
        Assert.Null(created.DecryptText(note[..^4] + "AAA="));
        Assert.Null(created.DecryptText("kein text"));
        Assert.Throws<VaultUnlockException>(() => UnlockedVault.Resume(created.VaultId, new byte[16], []));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var envelope = CreateEnvelope("Correct horse", out _);

        Assert.Throws<VaultUnlockException>(() => UnlockedVault.UnlockWithPassword(envelope, "correct horse", FastKdf));
    }

    [Fact]
    public void Recovery_code_unlocks_even_with_different_formatting()
    {
        var envelope = CreateEnvelope("pw", out var code);
        var sloppy = code.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();

        using var vault = UnlockedVault.UnlockWithRecoveryCode(envelope, sloppy);

        Assert.Equal(Payload, vault.Open(envelope));
    }

    [Fact]
    public void Changing_password_keeps_payload_and_invalidates_old_password()
    {
        var envelope = CreateEnvelope("old", out var code);
        using (var vault = UnlockedVault.UnlockWithPassword(envelope, "old", FastKdf))
        {
            vault.SetPassword("new", FastKdf);
            envelope = vault.Seal(vault.Open(envelope));
        }

        Assert.Throws<VaultUnlockException>(() => UnlockedVault.UnlockWithPassword(envelope, "old", FastKdf));
        using var reopened = UnlockedVault.UnlockWithPassword(envelope, "new", FastKdf);
        Assert.Equal(Payload, reopened.Open(envelope));
        using var recovered = UnlockedVault.UnlockWithRecoveryCode(envelope, code);
        Assert.Equal(Payload, recovered.Open(envelope));
    }

    [Fact]
    public void Regenerated_recovery_code_invalidates_old_one()
    {
        var envelope = CreateEnvelope("pw", out var oldCode);
        string newCode;
        using (var vault = UnlockedVault.UnlockWithPassword(envelope, "pw", FastKdf))
        {
            newCode = vault.RegenerateRecoveryCode();
            envelope = vault.Seal(vault.Open(envelope));
        }

        Assert.Throws<VaultUnlockException>(() => UnlockedVault.UnlockWithRecoveryCode(envelope, oldCode));
        using var recovered = UnlockedVault.UnlockWithRecoveryCode(envelope, newCode);
        Assert.Equal(Payload, recovered.Open(envelope));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var envelope = CreateEnvelope("pw", out _);
        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[0] ^= 1;
        var tampered = envelope with { Ciphertext = Convert.ToBase64String(bytes) };

        using var vault = UnlockedVault.UnlockWithPassword(envelope, "pw", FastKdf);
        Assert.Throws<VaultUnlockException>(() => vault.Open(tampered));
    }

    [Fact]
    public void Envelope_cannot_be_moved_to_another_vault_id()
    {
        var envelope = CreateEnvelope("pw", out _);
        var moved = envelope with { VaultId = Guid.NewGuid() };

        Assert.Throws<VaultUnlockException>(() => UnlockedVault.UnlockWithPassword(moved, "pw", FastKdf));
    }

    [Fact]
    public void Weakened_kdf_parameters_are_rejected()
    {
        var envelope = CreateEnvelope("pw", out _);
        var keys = envelope.Keys.Select(k => k.Kind == WrappedKeyKind.Password ? k with { Kdf = new KdfParameters(8, 1, 1) } : k).ToList();

        // Against the production minimum the fast test parameters are already too weak.
        Assert.Throws<VaultUnlockException>(() => UnlockedVault.UnlockWithPassword(envelope, "pw"));
        Assert.Throws<VaultUnlockException>(() => UnlockedVault.UnlockWithPassword(envelope with { Keys = keys }, "pw", FastKdf));
    }

    [Fact]
    public void Envelope_survives_json_round_trip_and_contains_no_plaintext()
    {
        var envelope = CreateEnvelope("pw", out _);

        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<VaultEnvelope>(json)!;

        Assert.DoesNotContain("geheim", json, StringComparison.Ordinal);
        using var vault = UnlockedVault.UnlockWithPassword(restored, "pw", FastKdf);
        Assert.Equal(Payload, vault.Open(restored));
    }

    [Fact]
    public void Recovery_code_round_trips()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();

        var code = RecoveryCode.Encode(secret);

        Assert.Equal(secret, RecoveryCode.Decode(code));
        Assert.Matches("^([0-9A-Z]{4}-){12}[0-9A-Z]{4}$", code);
    }

    private static VaultEnvelope CreateEnvelope(string password, out string recoveryCode)
    {
        using var vault = UnlockedVault.Create(password, out recoveryCode, FastKdf);
        return vault.Seal(Payload);
    }
}
