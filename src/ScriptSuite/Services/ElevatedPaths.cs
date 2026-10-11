namespace ScriptSuite.Services;

/// <summary>The three rendezvous paths for one elevated run: result JSON the
/// child writes, live log it streams, include list the parent prepares.
/// Built ONLY here — RunElevated and PreviewArgv share this function so the
/// previewed/tested paths can never drift from the spawned ones.</summary>
public sealed record ElevatedPaths(string ResultPath, string LiveLogPath, string IncludePath)
{
    public static ElevatedPaths Build()
    {
        string result = Path.Combine(AppPaths.RuntimeRoot,
            "scriptsuite_" + Guid.NewGuid().ToString("N") + ".json");
        return new ElevatedPaths(result, result + ".live.log", result + ".include.json");
    }
}
