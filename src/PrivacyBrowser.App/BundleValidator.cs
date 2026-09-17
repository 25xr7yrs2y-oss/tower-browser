using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyBrowser.App;

/// <summary>
/// Detects incomplete or mixed portable bundles before any trusted component is started.
/// The release checksum authenticates the archive; this manifest preserves component
/// identity after extraction and gives the UI a repairable, deterministic failure.
/// </summary>
public static class BundleValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task ValidateAsync(AppOptions options, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(options.BundleRoot, "bundle-manifest.json");
        var isPackagedLayout = PathsEqual(AppContext.BaseDirectory, options.BundleRoot);
        if (!File.Exists(manifestPath))
        {
            if (isPackagedLayout)
            {
                throw new InvalidOperationException(
                    "The portable bundle manifest is missing. Re-extract a verified Tower Browser package.");
            }
            return;
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The portable bundle manifest is empty.");
        if (string.IsNullOrWhiteSpace(manifest.ReleaseVersion) || manifest.Components.Count == 0)
        {
            throw new InvalidOperationException("The portable bundle manifest is incomplete.");
        }
        var applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        if (!string.Equals(applicationVersion, manifest.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application and portable bundle belong to different releases.");
        }

        var root = Path.GetFullPath(options.BundleRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var verifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(options.BundleRoot,
                component.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The portable bundle manifest contains an unsafe component path.");
            }
            if (!verifiedPaths.Add(path))
            {
                throw new InvalidOperationException("The portable bundle manifest contains a duplicate component path.");
            }
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Bundle component is missing: {component.Path}");
            }

            var info = new FileInfo(path);
            if (info.Length != component.Length)
            {
                throw new InvalidOperationException($"Bundle component size does not match the release: {component.Path}");
            }

            await using var componentStream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(componentStream, cancellationToken);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!actual.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Bundle component hash does not match the release: {component.Path}");
            }
        }

        if (isPackagedLayout)
        {
            foreach (var requiredPath in new[]
            {
                Path.GetFullPath(options.BrowserExe),
                Path.GetFullPath(options.BackendExe),
                Path.GetFullPath(Path.Combine(options.BundleRoot, "config", "policies.json")),
                Path.GetFullPath(Path.Combine(options.BundleRoot, "TowerBrowser.exe")),
            })
            {
                if (!verifiedPaths.Contains(requiredPath))
                {
                    throw new InvalidOperationException("A critical runtime component is not covered by the bundle manifest.");
                }
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

public sealed class BundleManifest
{
    [JsonPropertyName("releaseVersion")]
    public string ReleaseVersion { get; set; } = "";

    [JsonPropertyName("components")]
    public List<BundleComponent> Components { get; set; } = [];
}

public sealed class BundleComponent
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("length")]
    public long Length { get; set; }
}
