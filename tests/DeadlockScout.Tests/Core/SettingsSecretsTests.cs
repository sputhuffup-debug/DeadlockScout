using System.Text.Json;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Tests.Core;

public class SettingsSecretsTests
{
    // A stand-in for DPAPI: reversible, and only "this PC" (the same prefix) can read it back.
    private static string Protect(string plain) => "pc1." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
    private static string? Unprotect(string s) => s.StartsWith("pc1.") ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s[4..])) : null;

    private static ScoutSettings WithApi(string? key, string? stored) => new() { Api = new ApiSettings { ApiKey = key, ApiKeyProtected = stored } };

    [Fact]
    public void The_file_never_holds_the_plain_key()
    {
        var disk = SettingsSecrets.ForDisk(WithApi("secret-key", null), Protect);
        Assert.Null(disk.Api.ApiKey);
        Assert.StartsWith(SettingsSecrets.Prefix, disk.Api.ApiKeyProtected);
        var json = JsonSerializer.Serialize(disk, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("secret-key", json);
        Assert.Contains("\"apiKeyProtected\"", json);
    }

    [Fact]
    public void A_stored_key_is_decrypted_for_use()
    {
        var disk = SettingsSecrets.ForDisk(WithApi("secret-key", null), Protect);
        var loaded = SettingsSecrets.FromDisk(disk, Unprotect, out var state);
        Assert.Equal(SettingsSecrets.KeyState.Fine, state);
        Assert.Equal("secret-key", loaded.Api.ApiKey);
    }

    [Fact]
    public void A_plain_key_in_the_file_is_kept_and_flagged_for_encryption()
    {
        // An older version's file, or a key pasted into settings.json by hand; it wins over an older encrypted one.
        var loaded = SettingsSecrets.FromDisk(WithApi("  typed-key ", "dpapi1:pc1.b2xk"), Unprotect, out var state);
        Assert.Equal(SettingsSecrets.KeyState.PlainOnDisk, state);
        Assert.Equal("typed-key", loaded.Api.ApiKey);
        Assert.Null(SettingsSecrets.ForDisk(loaded, Protect).Api.ApiKey);
    }

    [Fact]
    public void A_key_from_another_PC_is_unreadable_and_left_alone()
    {
        var other = WithApi(null, SettingsSecrets.Prefix + "pc2.c2VjcmV0");
        var loaded = SettingsSecrets.FromDisk(other, Unprotect, out var state);
        Assert.Equal(SettingsSecrets.KeyState.Unreadable, state);
        Assert.Null(loaded.Api.ApiKey);
        // Saving other settings keeps the stored value as it was, rather than throwing it away.
        Assert.Equal(other.Api.ApiKeyProtected, SettingsSecrets.ForDisk(loaded, Protect).Api.ApiKeyProtected);
    }

    [Theory]
    [InlineData("pc1.c2VjcmV0")]          // no prefix
    [InlineData("dpapi1:not base64 !!")]  // damaged
    public void A_damaged_stored_key_is_unreadable(string stored)
    {
        string? Throwing(string s) => s.StartsWith("pc1.") ? Unprotect(s) : throw new FormatException();
        SettingsSecrets.FromDisk(WithApi(null, stored), Throwing, out var state);
        Assert.Equal(SettingsSecrets.KeyState.Unreadable, state);
    }

    [Fact]
    public void No_key_is_fine()
    {
        var loaded = SettingsSecrets.FromDisk(new ScoutSettings(), Unprotect, out var state);
        Assert.Equal(SettingsSecrets.KeyState.Fine, state);
        Assert.Null(loaded.Api.ApiKey);
        Assert.Null(SettingsSecrets.ForDisk(loaded, Protect).Api.ApiKeyProtected);
    }
}
