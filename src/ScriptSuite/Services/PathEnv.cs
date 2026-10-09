namespace ScriptSuite.Services;

/// <summary>User-path expansion shared by the settings UI and log routing.
/// Mirrors scripts/lib/common.sh expand_user_path exactly: leading ~ becomes
/// $HOME, then every $HOME / ${HOME} / $USER / ${USER} occurrence is replaced.
/// Anything else passes through untouched.</summary>
public static class PathEnv
{
    public static string HomeDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ExpandUserPath(string path, string? homeDir = null, string? user = null)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string home = homeDir
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? HomeDir;
        string who = user
            ?? Environment.GetEnvironmentVariable("USER")
            ?? Environment.UserName;
        string p = path;
        if (p == "~") return home;
        if (p.StartsWith("~/", StringComparison.Ordinal)) p = home + p[1..];
        p = p.Replace("$HOME", home);
        p = p.Replace("${HOME}", home);
        p = p.Replace("$USER", who);
        p = p.Replace("${USER}", who);
        return p;
    }

    /// <summary>XDG Documents fallback chain for file/folder picker start
    /// locations (Windows parity: MyDocuments initial dir).</summary>
    public static string DocumentsDir()
    {
        string docs = Path.Combine(HomeDir, "Documents");
        if (Directory.Exists(docs)) return docs;
        string xdg = Environment.GetEnvironmentVariable("XDG_DOCUMENTS_DIR") ?? "";
        if (!string.IsNullOrWhiteSpace(xdg) && Directory.Exists(xdg)) return xdg;
        return HomeDir;
    }
}
