using System.Security.Cryptography;
using DeadlockScout.Core.Updates;

// Release signing, run on the owner's PC (docs/release.md). The private key is written encrypted with a passphrase
// (PKCS#8, AES-256, 600,000 PBKDF2 rounds) and is never printed; keep it, and the backup key, off GitHub.
//
//   keygen <folder> <name>                       a key pair: <name>.private.pem (encrypted) and <name>.public.pem
//   sign <private.pem> <installer> <version> [--days 90] [--notes <url>]
//                                                release-manifest.json and .sig next to the installer
//   verify <folder> [<public.pem> ...]           checks a release folder against the given keys, or the app's built-in ones
//   fingerprint <public.pem>                     the key's fingerprint, to publish off GitHub
const int Rounds = 600_000;

try
{
    return args switch
    {
        ["keygen", var folder, var name] => KeyGen(folder, name),
        ["sign", var key, var installer, var version, .. var rest] => Sign(key, installer, version, rest),
        ["verify", var folder, .. var keys] => Verify(folder, keys),
        ["fingerprint", var pub] => Print(UpdateTrust.Fingerprint(File.ReadAllText(pub))),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"Failed: {ex.Message}");
    return 1;
}

static int KeyGen(string folder, string name)
{
    Directory.CreateDirectory(folder);
    string priv = Path.Combine(folder, $"{name}.private.pem"), pub = Path.Combine(folder, $"{name}.public.pem");
    if (File.Exists(priv) || File.Exists(pub)) { Console.Error.WriteLine($"{priv} or {pub} exists; not overwriting a key."); return 1; }
    var pass = Passphrase(confirm: true);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(priv, key.ExportEncryptedPkcs8PrivateKeyPem(pass,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Rounds)));
    var pubPem = key.ExportSubjectPublicKeyInfoPem();
    File.WriteAllText(pub, pubPem);
    Console.WriteLine($"Wrote {priv} (encrypted) and {pub}.");
    Console.WriteLine($"Fingerprint: {UpdateTrust.Fingerprint(pubPem)}");
    Console.WriteLine("Keep the private key off GitHub and backed up; publish the fingerprint somewhere other than GitHub.");
    return 0;
}

static int Sign(string keyPath, string installer, string version, string[] rest)
{
    int days = 90; string? notes = null;
    for (int i = 0; i + 1 < rest.Length; i += 2)
    {
        if (rest[i] == "--days") days = int.Parse(rest[i + 1]);
        else if (rest[i] == "--notes") notes = rest[i + 1];
    }
    if (!Version.TryParse(version, out _)) { Console.Error.WriteLine($"{version} is not a version like 1.0.0."); return 1; }
    using var key = ECDsa.Create();
    key.ImportFromEncryptedPem(File.ReadAllText(keyPath), Passphrase(confirm: false));
    var info = new FileInfo(installer);
    string sha;
    using (var s = info.OpenRead()) sha = UpdateTrust.Sha256(s);
    var now = DateTimeOffset.UtcNow;
    var manifest = new ReleaseManifest(ReleaseManifest.ProductName, version, now, now.AddDays(days), info.Name, sha, info.Length, notes);
    var bytes = manifest.ToBytes();
    var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    var dir = info.DirectoryName!;
    File.WriteAllBytes(Path.Combine(dir, ReleaseManifest.FileName), bytes);
    File.WriteAllText(Path.Combine(dir, ReleaseManifest.SignatureFileName), Convert.ToBase64String(signature) + "\n");
    Console.WriteLine($"Signed {info.Name} as {version} (sha256 {sha}, {info.Length:N0} bytes), valid until {manifest.Expires:yyyy-MM-dd}.");
    Console.WriteLine($"Upload {ReleaseManifest.FileName} and {ReleaseManifest.SignatureFileName} with the installer to the release.");
    return 0;
}

static int Verify(string folder, string[] keyFiles)
{
    var keys = keyFiles.Length > 0 ? keyFiles.Select(File.ReadAllText).ToList() : UpdateTrust.TrustedKeys.ToList();
    if (keys.Count == 0) { Console.Error.WriteLine("No key given and none built into the app yet."); return 1; }
    var bytes = File.ReadAllBytes(Path.Combine(folder, ReleaseManifest.FileName));
    var sig = UpdateTrust.ReadSignature(File.ReadAllText(Path.Combine(folder, ReleaseManifest.SignatureFileName))) ?? Array.Empty<byte>();
    var (verdict, m) = UpdatePolicy.Evaluate(bytes, sig, keys, new Version(0, 0), DateTimeOffset.UtcNow);
    if (verdict != UpdateVerdict.Offer || m is null) { Console.Error.WriteLine($"Refused: {verdict}."); return 1; }
    using var installer = File.OpenRead(Path.Combine(folder, m.Installer));
    if (!UpdatePolicy.InstallerMatches(m, installer)) { Console.Error.WriteLine($"Refused: {m.Installer} does not match the signed hash."); return 1; }
    Console.WriteLine($"OK: {m.Installer} is version {m.Version}, signed by a trusted key, valid until {m.Expires:yyyy-MM-dd}.");
    return 0;
}

static int Print(string text) { Console.WriteLine(text); return 0; }

static int Usage()
{
    Console.Error.WriteLine("ReleaseSign keygen <folder> <name> | sign <private.pem> <installer> <version> [--days 90] [--notes <url>] | verify <folder> [<public.pem> ...] | fingerprint <public.pem>");
    return 2;
}

// From DEADLOCKSCOUT_SIGN_PASSPHRASE when set (scripts), else typed without echo.
static string Passphrase(bool confirm)
{
    if (Environment.GetEnvironmentVariable("DEADLOCKSCOUT_SIGN_PASSPHRASE") is { Length: > 0 } fromEnv) return fromEnv;
    var first = Read("Passphrase: ");
    if (first.Length < 12) throw new ArgumentException("Use a passphrase of at least 12 characters.");
    if (confirm && Read("Again: ") != first) throw new ArgumentException("The passphrases differ.");
    return first;

    static string Read(string prompt)
    {
        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            text.Append(k.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
