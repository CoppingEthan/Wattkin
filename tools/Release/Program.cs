// Wattkin release tool (maintainer only).
//   keygen                         make the signing key (once) and print the public key for Releases.PublicKey
//   sign <exe> <version>           write <exe>.sig, and check it against the key built into the app
//   publish <version> <exe> [notes-file]   create the GitHub release v<version> and upload Wattkin.exe + .sig
//                                          (and Wattkin-portable.zip when it's next to the exe)
// The private key lives in %APPDATA%\Wattkin Release\release-key.pem, never in the repository.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wattkin.Features;

const string Repository = "CoppingEthan/Wattkin";
var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wattkin Release", "release-key.pem");

try
{
    return args.FirstOrDefault() switch
    {
        "keygen" => KeyGen(),
        "sign" when args.Length == 3 => Sign(args[1], args[2]),
        "publish" when args.Length is 3 or 4 => await Publish(args[1], args[2], args.Length == 4 ? File.ReadAllText(args[3]) : ""),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or CryptographicException or HttpRequestException or InvalidOperationException or TaskCanceledException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: keygen | sign <exe> <version> | publish <version> <exe> [notes-file]");
    return 2;
}

int KeyGen()
{
    if (File.Exists(keyPath))
    {
        // Never replace it: installed copies only accept updates signed by this key.
        Console.Error.WriteLine($"A release key already exists: {keyPath}");
        return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine($"Private key: {keyPath}  (back it up somewhere safe; it can't be recovered)");
    Console.WriteLine($"Public key for Releases.PublicKey: {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
    return 0;
}

int Sign(string exePath, string versionText)
{
    if (!Releases.TryParseVersion(versionText, out var version))
    {
        throw new InvalidOperationException($"'{versionText}' isn't a version.");
    }

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(keyPath));
    var exe = File.ReadAllBytes(exePath);
    var signature = Convert.ToBase64String(key.SignData(Releases.Statement(version, exe), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    if (!Releases.Verify(version, exe, signature))
    {
        throw new InvalidOperationException("The signature doesn't verify with the public key built into Wattkin (Releases.PublicKey). Wrong key?");
    }

    File.WriteAllText(exePath + ".sig", signature);
    Console.WriteLine($"Signed {Path.GetFileName(exePath)} {version.ToString(3)} -> {exePath}.sig");
    return 0;
}

async Task<int> Publish(string versionText, string exePath, string notes)
{
    if (!Releases.TryParseVersion(versionText, out var version))
    {
        throw new InvalidOperationException($"'{versionText}' isn't a version.");
    }

    var exe = File.ReadAllBytes(exePath);
    var signature = File.ReadAllText(exePath + ".sig");
    if (!Releases.Verify(version, exe, signature))
    {
        throw new InvalidOperationException("The exe and its .sig don't match. Sign it first.");
    }

    // The portable zip from the same build, when there is one (build.ps1 makes it next to the exe).
    var portablePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath))!, Releases.PortableAsset);
    var assets = new List<(string Name, byte[] Bytes, string Type)>
    {
        (Releases.ExeAsset, exe, "application/octet-stream"),
        (Releases.SignatureAsset, Encoding.ASCII.GetBytes(signature), "application/octet-stream"),
    };
    if (File.Exists(portablePath))
    {
        assets.Add((Releases.PortableAsset, File.ReadAllBytes(portablePath), "application/zip"));
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) }; // the portable zip is about 60 MB
    http.DefaultRequestHeaders.UserAgent.ParseAdd("wattkin-release");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GitHubToken());

    var tag = $"v{version.ToString(3)}";
    var request = JsonSerializer.Serialize(new { tag_name = tag, name = $"Wattkin {version.ToString(3)}", body = notes, draft = false, prerelease = false });
    using var created = await http.PostAsync($"https://api.github.com/repos/{Repository}/releases", new StringContent(request, Encoding.UTF8, "application/json"));
    var createdJson = await created.Content.ReadAsStringAsync();
    if (!created.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Creating release {tag} failed ({(int)created.StatusCode}): {createdJson}");
    }

    using var doc = JsonDocument.Parse(createdJson);
    var uploadUrl = doc.RootElement.GetProperty("upload_url").GetString()!.Split('{')[0];
    foreach (var (name, bytes, type) in assets)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var uploaded = await http.PostAsync($"{uploadUrl}?name={Uri.EscapeDataString(name)}", content);
        if (!uploaded.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Uploading {name} failed ({(int)uploaded.StatusCode}): {await uploaded.Content.ReadAsStringAsync()}");
        }
    }

    Console.WriteLine($"Published {doc.RootElement.GetProperty("html_url").GetString()}");
    return 0;
}

// The GitHub sign-in git already uses (Git Credential Manager), read without showing any prompt.
static string GitHubToken()
{
    var start = new ProcessStartInfo("git", "credential fill") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
    start.Environment["GCM_INTERACTIVE"] = "never";
    start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    using var git = Process.Start(start) ?? throw new InvalidOperationException("git isn't available.");
    git.StandardInput.Write("protocol=https\nhost=github.com\n\n");
    git.StandardInput.Close();
    var output = git.StandardOutput.ReadToEnd();
    git.WaitForExit();
    return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..]
        ?? throw new InvalidOperationException("No GitHub sign-in found for git. Sign in once with a git push.");
}
