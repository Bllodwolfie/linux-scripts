namespace ScriptSuite.Services;

/// <summary>Resolves the app's on-disk locations per the XDG Base Directory spec.
/// Config/state lives under ~/.config/scriptsuite, data (history.db) under
/// ~/.local/share/scriptsuite, runtime temp under $XDG_RUNTIME_DIR.
/// Every lookup honors the $XDG_* env var with the spec fallback, because
/// stock KDE Neon leaves all $XDG_* unset (verified during scoping).
/// Shipped assets (manifests, default configs, scripts) live next to the binary
/// in /usr/lib/scriptsuite (deb) or AppContext.BaseDirectory (dev).
/// Linux counterpart of windows-scripts AppPaths (%LOCALAPPDATA%\ScriptSuite).
/// </summary>
public static class AppPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string EnvOr(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    /// <summary>~/.config/scriptsuite — user config (configs/, *.json state).</summary>
    public static string ConfigRoot => EnvOr("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));

    public static string AppConfigRoot => Path.Combine(ConfigRoot, "scriptsuite");

    /// <summary>~/.local/share/scriptsuite — user data (history.db).</summary>
    public static string DataRoot => Path.Combine(
        EnvOr("XDG_DATA_HOME", Path.Combine(Home, ".local", "share")), "scriptsuite");

    /// <summary>$XDG_RUNTIME_DIR with /tmp fallback — live-log/result temp files.</summary>
    public static string RuntimeRoot
    {
        get
        {
            var r = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrWhiteSpace(r) ? Path.GetTempPath() : r;
        }
    }

    public static string ConfigsDir => Path.Combine(AppConfigRoot, "configs");

    public static string DashboardStatePath => Path.Combine(AppConfigRoot, "dashboard.json");

    public static string WizardStatePath => Path.Combine(AppConfigRoot, "wizard.json");

    public static string HistoryDbPath => Path.Combine(DataRoot, "history.db");

    public static string SchedulesPath => Path.Combine(AppConfigRoot, "schedules.json");
    public static string RiskConsentsPath => Path.Combine(AppConfigRoot, "risk-consents.json");
    public static string ThemePath => Path.Combine(AppConfigRoot, "theme.json");

    /// <summary>Shipped manifests: /usr/lib/scriptsuite/Manifests in the .deb,
    /// AppContext.BaseDirectory/Manifests in dev. Probes both.</summary>
    public static string ManifestsDir => FindShippedDir("Manifests");

    public static string DefaultConfigsDir => FindShippedDir("DefaultConfigs");

    private static string FindShippedDir(string leaf)
    {
        var dev = Path.Combine(AppContext.BaseDirectory, leaf);
        if (Directory.Exists(dev))
            return dev;
        var system = Path.Combine("/usr", "lib", "scriptsuite", leaf);
        return system;
    }

    public static string ConfigPathFor(string scriptId) => Path.Combine(ConfigsDir, scriptId + ".json");

    /// <summary>Copies the shipped default config JSON for each script into
    /// ~/.config/scriptsuite/configs on first run. Existing files are never
    /// overwritten. Mirrors EnsureConfigsSeeded on Windows.</summary>
    public static void EnsureConfigsSeeded()
    {
        Directory.CreateDirectory(ConfigsDir);
        if (!Directory.Exists(DefaultConfigsDir))
            return;
        foreach (var file in Directory.GetFiles(DefaultConfigsDir, "*.json"))
        {
            var dest = Path.Combine(ConfigsDir, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Copy(file, dest);
        }
    }
}
