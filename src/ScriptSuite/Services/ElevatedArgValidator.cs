using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScriptSuite.Services;

/// <summary>Caller identity for privileged-path confinement: the uid pkexec
/// authenticated (PKEXEC_UID) plus that user's home directory.</summary>
public sealed record ElevatedCaller(uint Uid, string Home);

/// <summary>Validates --elevated-run argv in the CHILD (not only the parent),
/// because polkit authorizes the binary + argv1 only — argv2+ is attacker
/// controlled when the child is invoked directly (G-1). Two tiers:
///   Always: known script id (G-1), elevation-eligible manifest (G-6),
///     caller risk consent re-checked (not just the parent), strict
///     --include-only JSON (never widen to unrestricted, G-7).
///   Privileged only (real pkexec: euid 0 + PKEXEC_UID set, or the test hook
///     below): path confinement for --config-path/--result-path/--live-log
///     (G-2/G-3/G-5) — realpath prefix + caller ownership + no symlinks +
///     exclusive creates. Unprivileged direct runs (self-tests) need no
///     confinement: the process has no privilege its caller lacks.
/// Test hook SCRIPTSUITE_FORCE_PRIVILEGED_CHECK=1 forces the privileged tier
/// with caller = (geteuid, $HOME). Strictness-only: it can never loosen a
/// check, so it is safe to ship.</summary>
public static class ElevatedArgValidator
{
    public const string ForceEnv = "SCRIPTSUITE_FORCE_PRIVILEGED_CHECK";
    private const long MaxAuxFileBytes = 1_000_000;

    // Parent-generated result names: scriptsuite_<32 hex>.json [+ .live.log |
    // .include.json]. The fixed shape (not just a prefix) keeps --result-path
    // /etc/anything and /tmp/planted-name out even when the directory passes.
    private static readonly Regex OutputName =
        new(@"^scriptsuite_[0-9a-f]{32}\.json(\.live\.log|\.include\.json)?$", RegexOptions.Compiled);

    /// <summary>Returns the privileged caller, or null for unprivileged
    /// direct runs (no pkexec, no hook — nothing to confine).</summary>
    public static ElevatedCaller? TryGetPrivilegedCaller()
    {
        if (Environment.GetEnvironmentVariable(ForceEnv) == "1")
            return new ElevatedCaller(NativeLib.EffectiveUid(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string? pkexecUid = Environment.GetEnvironmentVariable("PKEXEC_UID");
        if (pkexecUid is null || !uint.TryParse(pkexecUid, out uint uid) || uid == 0)
            return null;
        if (NativeLib.EffectiveUid() != 0)
            return null; // PKEXEC_UID set but not root: not the pkexec child
        return new ElevatedCaller(uid, HomeOf(uid)
            ?? throw new InvalidOperationException($"Cannot resolve home directory for PKEXEC_UID={uid}."));
    }

    private static string? HomeOf(uint uid)
    {
        // passwd line: name:pw:uid:gid:gecos:home:shell
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length >= 7 && uint.TryParse(f[2], out uint u) && u == uid && f[5].Length > 0)
                    return f[5];
            }
        }
        catch { }
        return null;
    }

    /// <summary>Runs every check; throws InvalidOperationException (message =
    /// user-facing reason) on the first failure. No side effects.</summary>
    public static void Validate(string scriptId, string? configPath, string? resultPath,
        string? liveLogPath, string? includeOnlyPath,
        ManifestCatalog catalog, ElevatedCaller? caller)
    {
        var manifest = catalog.Find(scriptId)
            ?? throw new InvalidOperationException($"Unknown script id: '{scriptId}'.");
        if (!manifest.RequiresAdmin)
            throw new InvalidOperationException(
                $"Script '{scriptId}' is not elevation-eligible (requiresAdmin=false).");

        // Strict include-only in BOTH tiers: present-but-garbage aborts the
        // run; it must never silently widen to unrestricted (G-7). Missing
        // file = unrestricted (parent always sends one; direct runs opt out).
        if (includeOnlyPath is not null && File.Exists(includeOnlyPath))
            ReadIncludeFile(includeOnlyPath);

        if (caller is null)
            return; // unprivileged direct run (self-tests): no privilege is
                    // conferred — the caller could run the .sh itself — so
                    // consent + path confinement are the privileged tier only.

        // Privileged tier: the child acts as root for the CALLER, so the run
        // must correspond to that caller's in-app consent (not just the
        // parent's word for it), and every path it opens is confined.
        string consentPath = Path.Combine(caller.Home, ".config", "scriptsuite", "risk-consents.json");
        if (!new RiskConsentStore(consentPath).HasConsent(scriptId))
            throw new InvalidOperationException(
                $"Risk consent required for '{scriptId}' — confirm 'I understand the risks' first.");

        if (configPath is null)
            throw new InvalidOperationException("Privileged run requires --config-path.");
        ValidateConfigPath(configPath, caller);
        if (resultPath is null)
            throw new InvalidOperationException("Privileged run requires --result-path.");
        ValidateOutputPath(resultPath, caller, "result");
        if (liveLogPath is not null)
            ValidateOutputPath(liveLogPath, caller, "live log");
    }

    /// <summary>Reads + strictly validates an include-only file (JSON string
    /// array, size-capped, no symlink followed). Throws on any failure.</summary>
    public static List<string> ReadIncludeFile(string path)
    {
        if (new FileInfo(path).Length > MaxAuxFileBytes)
            throw new InvalidOperationException($"Include-only file '{path}' exceeds size limit.");
        using var fs = NativeLib.OpenNoFollow(path);
        using var sr = new StreamReader(fs);
        List<string>? list;
        try { list = JsonSerializer.Deserialize<List<string>>(sr.ReadToEnd()); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"--include-only file '{path}' is not a JSON string array ({ex.Message}) — aborting, never widening to unrestricted.");
        }
        if (list is null)
            throw new InvalidOperationException($"--include-only file '{path}' is not a JSON string array — aborting.");
        return list;
    }

    private static void ValidateConfigPath(string path, ElevatedCaller caller)
    {
        if (!Path.IsPathRooted(path))
            throw new InvalidOperationException($"Config path '{path}' must be absolute.");
        if (NativeLib.IsSymlink(path))
            throw new InvalidOperationException($"Config path '{path}' is a symlink — refusing.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Config file '{path}' does not exist.");
        if (new FileInfo(path).Length > MaxAuxFileBytes)
            throw new InvalidOperationException($"Config file '{path}' exceeds size limit.");
        var owner = NativeLib.StatOwner(path)?.Uid;
        if (owner != caller.Uid && owner != 0)
            throw new InvalidOperationException($"Config file '{path}' is not owned by the calling user — refusing.");
        string? real = NativeLib.RealPath(path);
        string configDir = Path.Combine(caller.Home, ".config", "scriptsuite") + Path.DirectorySeparatorChar;
        string shippedDev = Path.Combine(AppContext.BaseDirectory, "DefaultConfigs") + Path.DirectorySeparatorChar;
        const string shippedSys = "/usr/lib/scriptsuite/DefaultConfigs/";
        if (real is null || (!real.StartsWith(configDir, StringComparison.Ordinal)
            && !real.StartsWith(shippedDev, StringComparison.Ordinal)
            && !real.StartsWith(shippedSys, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Config file '{path}' is outside the caller's scriptsuite config dir — refusing (G-5 confused deputy).");
    }

    /// <summary>True when dir EQUALS baseDir or lies strictly beneath it,
    /// compared at component boundaries — never by raw string prefix, so
    /// "/run/user/1000x" is NOT beneath "/run/user/1000" and the base dir
    /// itself always matches (a trailing-separator StartsWith misses it).
    /// Internal for the Stage 4 truth table (same assembly).</summary>
    internal static bool IsSameOrBeneath(string dir, string baseDir)
    {
        string d = dir.TrimEnd(Path.DirectorySeparatorChar);
        string b = baseDir.TrimEnd(Path.DirectorySeparatorChar);
        return d.Equals(b, StringComparison.Ordinal)
            || d.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void ValidateOutputPath(string path, ElevatedCaller caller, string role)
    {
        if (!Path.IsPathRooted(path))
            throw new InvalidOperationException($"Elevated {role} path '{path}' must be absolute.");
        if (NativeLib.IsSymlink(path))
            throw new InvalidOperationException($"Elevated {role} path '{path}' is a symlink — refusing.");
        string name = Path.GetFileName(path);
        if (!OutputName.IsMatch(name))
            throw new InvalidOperationException(
                $"Elevated {role} path '{path}' must look like scriptsuite_<32 hex>.json — refusing.");
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException(
                $"Elevated {role} path '{path}' already exists — refusing to open (plant).");
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            throw new InvalidOperationException($"Elevated {role} directory '{dir}' does not exist.");
        if (NativeLib.IsSymlink(dir))
            throw new InvalidOperationException($"Elevated {role} directory '{dir}' is a symlink — refusing.");
        string? realDir = NativeLib.RealPath(dir);
        string runUserDir = $"/run/user/{caller.Uid}";
        string tmpDir = Path.GetTempPath(); // /tmp/ fallback when XDG_RUNTIME_DIR is unset
        if (realDir is null || (!IsSameOrBeneath(realDir, runUserDir) && !IsSameOrBeneath(realDir, tmpDir)))
            throw new InvalidOperationException(
                $"Elevated {role} directory '{dir}' is outside the caller's runtime dirs — refusing.");
        // Owner gate: caller-owned normally; the sticky system temp dir is
        // allowed root-owned (parent's documented XDG fallback — O_EXCL plus
        // the GUID name still defeat plants there).
        uint? owner = NativeLib.StatOwner(realDir)?.Uid;
        bool isTempRoot = string.Equals(realDir.TrimEnd(Path.DirectorySeparatorChar),
            tmpDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        if (owner != caller.Uid && !(isTempRoot && owner == 0))
            throw new InvalidOperationException(
                $"Elevated {role} directory '{dir}' is not owned by the calling user — refusing.");
    }
}
