namespace ScriptSuite.Services;

/// <summary>Last-chance crash recorder: a full exception trace to a local
/// timestamped file (nothing is ever sent anywhere). A user-closed window is
/// a normal exit and writes nothing — this fires only on unhandled
/// exceptions, which previously died as a bare "Unhandled exception." line
/// with no trace (seen twice on 2026-10-12).</summary>
public static class CrashLog
{
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                if (e.ExceptionObject is Exception ex) Write(ex);
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Write(e.Exception); }
            catch { }
            // Deliberately not SetObserved: default runtime behavior is kept.
        };
    }

    /// <summary>Writes ex.ToString() (message + full stack + inner exceptions)
    /// to DataRoot/crash-&lt;utc-timestamp&gt;.log. Never overwrites: a numeric
    /// suffix is appended on collision. Returns the path.</summary>
    public static string Write(Exception ex)
    {
        string dir = AppPaths.DataRoot;
        Directory.CreateDirectory(dir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff");
        string path = Path.Combine(dir, $"crash-{stamp}.log");
        for (int i = 1; File.Exists(path) && i < 1000; i++)
            path = Path.Combine(dir, $"crash-{stamp}-{i}.log");
        File.WriteAllText(path,
            $"ScriptSuite unhandled exception (local only, nothing sent){Environment.NewLine}"
            + $"UTC: {DateTime.UtcNow:O}{Environment.NewLine}"
            + $"Process: {Environment.ProcessId}{Environment.NewLine}{Environment.NewLine}"
            + ex.ToString() + Environment.NewLine);
        return path;
    }
}
