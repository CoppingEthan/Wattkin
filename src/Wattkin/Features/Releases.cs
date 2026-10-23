using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wattkin.Features;

/// <summary>A published GitHub release that carries a signed Wattkin build.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri ExeUrl, long ExeSize, Uri SignatureUrl, string? Notes, Uri? Page);

/// <summary>
/// Reads GitHub's "latest release" answer and checks release signatures. Pure logic, unit tested.
///
/// A release is trusted only when "Wattkin {version} sha256:{hash of the exe}" is signed by the release
/// key whose public half is built in below. The private half stays on the maintainer's PC (never in
/// the repository), so neither GitHub nor someone with access to the account can push code to
/// installed copies: an update that doesn't verify is never written to disk as the program.
/// </summary>
public static class Releases
{
    public const string ExeAsset = "Wattkin.exe";
    public const string SignatureAsset = "Wattkin.exe.sig";

    /// <summary>The portable version (DataFolders in the app). It doesn't update itself, so it isn't read here.</summary>
    public const string PortableAsset = "Wattkin-portable.zip";
    public const long MaxExeBytes = 64L * 1024 * 1024;
    public const int MaxSignatureBytes = 1024;

    /// <summary>ECDSA P-256 public release key (SubjectPublicKeyInfo, base64). tools/Release keygen prints it.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE/9lhBmVKm3Gb2bAwmb2semafHeXKRrsXydcKHXYi87ZPYS+bt7M/eyv3zp9Y+DgWlDbK70jyhK5gZn6pcgiaRw==";

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>"v0.2.0", "0.2.0" or "0.2.0+commit" → 0.2.0.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var s = text?.Trim() ?? "";
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        if (!Version.TryParse(s, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    public static ReleaseInfo? Parse(string json, out string? problem)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "GitHub's answer couldn't be read.";
                return null;
            }

            if (Bool(root, "draft") || Bool(root, "prerelease"))
            {
                problem = "The latest release isn't a final release.";
                return null;
            }

            var tag = Text(root, "tag_name");
            if (!TryParseVersion(tag, out var version))
            {
                problem = $"The latest release ({tag ?? "no tag"}) doesn't have a version number.";
                return null;
            }

            Uri? exe = null, signature = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    {
                        continue;
                    }

                    if (name == ExeAsset)
                    {
                        exe = url;
                        size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    }
                    else if (name == SignatureAsset)
                    {
                        signature = url;
                    }
                }
            }

            if (exe is null || signature is null)
            {
                problem = $"Release {tag} has no signed {ExeAsset}.";
                return null;
            }

            if (size is <= 0 or > MaxExeBytes)
            {
                problem = $"Release {tag} has an unexpected file size.";
                return null;
            }

            Uri.TryCreate(Text(root, "html_url"), UriKind.Absolute, out var page);
            problem = null;
            return new ReleaseInfo(version, tag!, exe, size, signature, Text(root, "body"), page);
        }
        catch (JsonException)
        {
            problem = "GitHub's answer couldn't be read.";
            return null;
        }
    }

    /// <summary>The exact bytes the release key signs.</summary>
    public static byte[] Statement(Version version, ReadOnlySpan<byte> exe) =>
        Encoding.ASCII.GetBytes($"Wattkin {Normalize(version).ToString(3)} sha256:{Convert.ToHexStringLower(SHA256.HashData(exe))}");

    /// <param name="signature">The .sig file: a base64 IEEE P1363 ECDSA P-256 signature of <see cref="Statement"/>.</param>
    public static bool Verify(Version version, ReadOnlySpan<byte> exe, string signature, string publicKey = PublicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.KeySize == 256
                && key.VerifyData(Statement(version, exe), Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
