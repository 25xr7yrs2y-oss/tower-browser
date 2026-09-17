namespace PrivacyBrowser.App;

public sealed record AppOptions(
    string BundleRoot,
    string BrowserExe,
    string BackendExe,
    string ProfilePath,
    string InitialUrl,
    IReadOnlyList<string> AdditionalBrowserArguments,
    bool KeepBackendRunning,
    bool SkipBackendLaunch)
{
    public static AppOptions Parse(string[] args)
    {
        var bundleRoot = FindBundleRoot();
        string? browserExe = null;
        string? backendExe = null;
        string? profilePath = null;
        var initialUrl = "about:blank";
        var browserArgs = new List<string>();
        var keepBackend = false;
        var skipBackend = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value after {args[i]}.");

            switch (args[i].ToLowerInvariant())
            {
                case "--bundle-root": bundleRoot = Path.GetFullPath(Value()); break;
                case "--browser-exe": browserExe = Path.GetFullPath(Value()); break;
                case "--backend-exe": backendExe = Path.GetFullPath(Value()); break;
                case "--profile": profilePath = Path.GetFullPath(Value()); break;
                case "--initial-url": initialUrl = Value(); break;
                case "--browser-arg": browserArgs.Add(Value()); break;
                case "--keep-backend-running": keepBackend = true; break;
                case "--skip-backend-launch": skipBackend = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        browserExe ??= Path.Combine(bundleRoot, "vendor", "mullvad-browser", "mullvadbrowser.exe");
        backendExe ??= Path.Combine(bundleRoot, "vendor", "myst-lmprove", "resources", "app.asar.unpacked",
            "node_modules", "@mysteriumnetwork", "node", "bin", "win", "x64", "myst.exe");
        profilePath ??= Path.Combine(bundleRoot, "state", "profile");

        return new AppOptions(bundleRoot, browserExe, backendExe, profilePath, initialUrl,
            browserArgs, keepBackend, skipBackend);
    }

    private static string FindBundleRoot()
    {
        var configured = Environment.GetEnvironmentVariable("TOWER_BROWSER_ROOT");
        // Preserve the legacy variable for existing development setups.
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("PRIVACY_BROWSER_ROOT");
        }
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "policies.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
