using System.Diagnostics;
using System.Text.Json;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>Ownership handoff for the elevated rendezvous files: production
/// fchowns the OPEN fd (never path-based — no TOCTOU, no symlink race);
/// tests substitute a recorder. The handoff runs only when the child is
/// really root; unprivileged round-trips skip it (files are already ours).</summary>
public interface IRendezvousOwner
{
    void OwnFile(FileStream fs, uint uid, uint gid);
}

/// <summary>Production handoff: fchown(2) on the open stream's fd.</summary>
public sealed class FdRendezvousOwner : IRendezvousOwner
{
    public static readonly FdRendezvousOwner Instance = new();
    public void OwnFile(FileStream fs, uint uid, uint gid) =>
        NativeLib.FChown(fs.SafeFileHandle!, uid, gid);
}

/// <summary>
/// Runs admin-required scripts elevated via pkexec. The flow mirrors
/// windows-scripts ScriptExecutor.RunElevated (runas) one-for-one, with the
/// platform primitive swapped:
///   1. Risk-consent gate (no consent → Cancelled, nothing spawned).
///   2. Temp files under $XDG_RUNTIME_DIR: result JSON, live log, include list.
///   3. Spawn `pkexec <self> --elevated-run …`; tail the live log for streaming.
///   4. Read back the result JSON the child wrote; map pkexec 126/127
///      (dismissed/failed authorization) to Cancelled — the analogue of UAC
///      decline (Win32 1223).
/// The child entrypoint is Program.cs (--elevated-run): same binary, headless,
/// explicit absolute paths throughout (pkexec scrubs the environment, so
/// nothing may depend on $HOME/$XDG_* — and nothing does).
/// </summary>
public sealed class PolkitElevationService
{
    private const int LivePollMs = 150;
    private readonly ManifestCatalog _catalog;
    private readonly RiskConsentStore _consents;

    /// <summary>Binary re-launched via pkexec. Defaults to this process
    /// (production single-binary layout); tests point it at a built
    /// ScriptSuite binary since the harness exe cannot serve --elevated-run.</summary>
    public string HelperPath { get; set; } = Environment.ProcessPath ?? string.Empty;

    public PolkitElevationService(ManifestCatalog catalog, RiskConsentStore consents)
    {
        _catalog = catalog;
        _consents = consents;
    }

    /// <summary>Runs an admin script elevated via pkexec. includeOnly narrows
    /// the run to the listed Targets; onLogLine receives live-log lines.
    /// A cancelled token attempts to kill the pkexec tree — note a root-owned
    /// child may refuse SIGTERM from the user, in which case the run completes
    /// and its real outcome is reported. No result file + cancel = Cancelled.</summary>
    public ScriptRunResult RunElevated(string scriptId, string configPath,
        IReadOnlyList<string>? includeOnly = null, Action<string>? onLogLine = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = _catalog.Find(scriptId)
            ?? throw new InvalidOperationException($"Manifest not found: {scriptId}");
        if (!manifest.RequiresAdmin)
            throw new InvalidOperationException($"RunElevated called for {scriptId}, which does not require admin.");

        if (!_consents.HasConsent(scriptId))
        {
            return new ScriptRunResult
            {
                Outcome = RunOutcome.Cancelled,
                ScriptId = scriptId,
                Logs = new List<string> { $"Cancelled: risk consent required for {scriptId} — confirm 'I understand the risks' first." },
            };
        }

        var paths = ElevatedPaths.Build();
        string resultPath = paths.ResultPath;
        string liveLog = paths.LiveLogPath;
        string includePath = paths.IncludePath;
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.WriteAllText(includePath, JsonSerializer.Serialize(includeOnly is { Count: > 0 }
            ? includeOnly.ToList()
            : new List<string>()));

        string self = HelperPath;
        if (string.IsNullOrEmpty(self))
            throw new InvalidOperationException("Environment.ProcessPath is null.");

        var psi = new ProcessStartInfo
        {
            FileName = "pkexec",
            RedirectStandardOutput = true,
            // Captured so a refusal/crash reason reaches the Output pane
            // (the child prints refusals here, never to the result file).
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(self);
        psi.ArgumentList.Add("--elevated-run");
        psi.ArgumentList.Add(scriptId);
        psi.ArgumentList.Add("--config-path");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("--result-path");
        psi.ArgumentList.Add(resultPath);
        psi.ArgumentList.Add("--live-log");
        psi.ArgumentList.Add(liveLog);
        psi.ArgumentList.Add("--include-only");
        psi.ArgumentList.Add(includePath);

        Process child;
        try
        {
            child = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            TryDelete(includePath);
            return new ScriptRunResult
            {
                Outcome = RunOutcome.Failed,
                ScriptId = scriptId,
                Logs = new List<string> { "Failed to launch pkexec: " + ex.Message },
            };
        }

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { child.Kill(entireProcessTree: true); } catch { }
        });

        if (onLogLine != null)
        {
            int emitted = 0;
            string carry = "";
            while (!child.HasExited && !cancellationToken.IsCancellationRequested)
            {
                (emitted, carry) = PumpLiveLog(liveLog, emitted, carry, onLogLine);
                Thread.Sleep(LivePollMs);
            }
            (emitted, carry) = PumpLiveLog(liveLog, emitted, carry, onLogLine);
        }

        try { child.StandardOutput.ReadToEnd(); } catch { }
        child.WaitForExit();
        var childStderr = new List<string>();
        try
        {
            foreach (var line in child.StandardError.ReadToEnd()
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                childStderr.Add(line.TrimEnd('\r'));
        }
        catch { }
        TryDelete(liveLog);
        TryDelete(includePath);

        // Cancel with no result file: the tree died before writing anything.
        // If a result file exists the run genuinely finished — report it.
        if (cancellationToken.IsCancellationRequested && !File.Exists(resultPath))
        {
            return new ScriptRunResult
            {
                Outcome = RunOutcome.Cancelled,
                ScriptId = scriptId,
                Logs = new List<string> { "Cancelled by user." },
            };
        }

        // pkexec 126/127 = dismissed or failed authorization (the polkit
        // analogue of declining UAC). Never report those as Failed.
        if (child.ExitCode == 126 || child.ExitCode == 127)
        {
            TryDelete(resultPath);
            return new ScriptRunResult
            {
                Outcome = RunOutcome.Cancelled,
                ScriptId = scriptId,
                Logs = new List<string> { $"Cancelled: polkit authorization was dismissed or failed (pkexec exit {child.ExitCode})." },
            };
        }

        if (!File.Exists(resultPath))
        {
            // Surface the reason in the Output pane, not just History: pump
            // the child's stderr through the live-log callback too.
            var logs = NoResultLogs(child.ExitCode, childStderr);
            if (onLogLine != null)
                foreach (var line in logs)
                    try { onLogLine(line); } catch { }
            return new ScriptRunResult
            {
                Outcome = RunOutcome.Failed,
                ScriptId = scriptId,
                Logs = logs,
            };
        }

        try
        {
            return ReadResultFile(resultPath, scriptId);
        }
        catch (Exception ex)
        {
            // Transport ok (the file exists) but the payload is unreadable —
            // e.g. a root-owned 0600 result from a pre-handoff child, or a
            // damaged file. Never let the raw exception (typically
            // UnauthorizedAccessException) escape to the UI as "Run error":
            // exit 0 finished the work (Warning: verify via journal/export),
            // anything else Failed with the reason.
            return MapUnreadableResult(scriptId, child.ExitCode, ex);
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    /// <summary>The elevated child's entire job: run one script's execute
    /// phase headless and write the result JSON. Returns the process exit
    /// code (0 = transport ok; the payload carries the run outcome).
    /// Rendezvous files the child creates O_EXCL|O_NOFOLLOW are fchowned to
    /// PKEXEC_UID:primary-gid on the open fd when really root, so the
    /// unprivileged parent can read them back; anything else (unprivileged
    /// round-trip) skips the handoff. A root child without a trustworthy
    /// PKEXEC_UID refuses loudly (exit 2) rather than leaving root-owned
    /// files behind. owner/getEuid are test seams (default: production).</summary>
    public int RunElevatedChild(string scriptId, string configPath, string resultPath,
        string? liveLogPath, string? includeOnlyPath,
        IRendezvousOwner? owner = null, Func<uint>? getEuid = null)
    {
        uint euid = (getEuid ?? NativeLib.EffectiveUid)();
        IRendezvousOwner rendezvousOwner = owner ?? FdRendezvousOwner.Instance;
        var payload = new Dictionary<string, object?>
        {
            ["ScriptId"] = scriptId,
            ["Outcome"] = "Failed",
            ["Logs"] = new List<string>(),
            ["ItemCounts"] = new Dictionary<string, int>(),
            ["ExitCode"] = 1,
        };
        try
        {
            // Strict re-read (validated by Program.cs before the run; a swap
            // since then fails closed here instead of widening, G-7).
            IReadOnlyList<string>? includeOnly = null;
            if (includeOnlyPath is not null && File.Exists(includeOnlyPath))
                includeOnly = ElevatedArgValidator.ReadIncludeFile(includeOnlyPath);

            // Live log: exclusive create, single held stream. A pre-existing
            // path (plant) aborts the run — we never append through it (G-3).
            // Root hands the fd to the caller right after create (C2), so the
            // parent can stream it; handoff refusal deletes what we created
            // (never anyone else's file) and aborts.
            StreamWriter? live = null;
            if (liveLogPath is not null)
            {
                FileStream? liveFs = null;
                try
                {
                    liveFs = NativeLib.CreateExclusive(liveLogPath);
                    OwnRendezvous(liveFs, liveLogPath, euid, rendezvousOwner);
                    live = new StreamWriter(liveFs)
                    {
                        AutoFlush = true,
                    };
                    liveFs = null; // owned by the writer now
                }
                catch (InvalidOperationException ex)
                {
                    if (liveFs is not null)
                    {
                        try { liveFs.Dispose(); } catch { }
                        liveFs = null;
                        TryDelete(liveLogPath);
                    }
                    Console.Error.WriteLine("Elevated child refused: " + ex.Message);
                    return 2;
                }
            }

            Action<string>? onLine = null;
            if (live is not null)
                onLine = line =>
                {
                    try { live.WriteLine(line); }
                    catch { /* best effort */ }
                };

            try
            {
                var executor = new BashScriptExecutor(_catalog);
                var result = executor.Execute(scriptId, configPath, dryRun: false, includeOnly, onLine);
                payload["Outcome"] = OutcomeToString(result.Outcome);
                payload["Logs"] = result.Logs;
                payload["ItemCounts"] = result.ItemCounts;
                payload["ExitCode"] = 0;
            }
            finally
            {
                try { live?.Dispose(); } catch { }
            }
        }
        catch (Exception ex)
        {
            payload["Outcome"] = "Failed";
            payload["Logs"] = new List<string> { "ELEVATED CHILD ERROR: " + ex.Message };
            payload["ExitCode"] = -1;
        }

        // Exclusive create: never truncate a planted file, never crash (the
        // old File.WriteAllText outside try SIGABRTed on hostile paths).
        // Root fchowns the open fd to the caller before writing (C2); a
        // refused handoff removes what we created and exits 2 with no result
        // file, so the parent takes the clean no-result path.
        try
        {
            using (var fs = NativeLib.CreateExclusive(resultPath))
            {
                try { OwnRendezvous(fs, resultPath, euid, rendezvousOwner); }
                catch (InvalidOperationException) { TryDelete(resultPath); throw; }
                using var sw = new StreamWriter(fs);
                sw.Write(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("Elevated child refused: " + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Elevated child failed to write result: " + ex.Message);
            return 1;
        }
        return 0;
    }

    /// <summary>Hands an exclusively-created rendezvous fd to the pkexec
    /// caller: euid==0 only; PKEXEC_UID must be numeric and must own the
    /// rendezvous directory (else we would hand files to a stranger); the
    /// group is the caller's primary gid. Anything untrustworthy throws
    /// InvalidOperationException (refusal class, never a crash).</summary>
    private static void OwnRendezvous(FileStream fs, string path, uint euid,
        IRendezvousOwner owner)
    {
        if (euid != 0) return; // unprivileged round-trip: files are already ours
        string? uidRaw = Environment.GetEnvironmentVariable("PKEXEC_UID");
        if (!uint.TryParse(uidRaw, out uint uid))
            throw new InvalidOperationException(
                "running as root without a numeric PKEXEC_UID — refusing to leave root-owned rendezvous files.");
        var st = Path.GetDirectoryName(path) is string dir ? NativeLib.StatOwner(dir) : null;
        if (st is null || st.Value.Uid != uid)
            throw new InvalidOperationException(
                "PKEXEC_UID does not own the rendezvous directory — refusing to hand files to a stranger.");
        uint gid = NativeLib.PrimaryGid(uid)
            ?? throw new InvalidOperationException(
                $"unknown primary group for uid {uid} — refusing to hand off with a wrong group.");
        owner.OwnFile(fs, uid, gid);
    }

    /// <summary>Dry-run of the pkexec command line: returns the exact argv that
    /// RunElevated would spawn, without creating temp files or spawning
    /// anything. For verifying construction before a real authorization.</summary>
    public IReadOnlyList<string> PreviewArgv(string scriptId, string configPath,
        IReadOnlyList<string>? includeOnly = null)
    {
        // Same identity gate as the child: unknown/non-admin ids are rejected
        // at preview time, not at authorization time.
        var manifest = _catalog.Find(scriptId)
            ?? throw new InvalidOperationException($"Unknown script id: '{scriptId}'.");
        if (!manifest.RequiresAdmin)
            throw new InvalidOperationException(
                $"Script '{scriptId}' is not elevation-eligible (requiresAdmin=false).");
        string self = HelperPath;
        if (string.IsNullOrEmpty(self))
            throw new InvalidOperationException("Environment.ProcessPath is null.");
        // Real production paths (same builder RunElevated spawns with — never
        // placeholders), so previews and tests cannot drift from the spawn.
        var preview = ElevatedPaths.Build();
        var argv = new List<string>
        {
            "pkexec", self,
            "--elevated-run", scriptId,
            "--config-path", configPath,
            "--result-path", preview.ResultPath,
            "--live-log", preview.LiveLogPath,
            "--include-only", preview.IncludePath,
        };
        return argv;
    }

    /// <summary>Result for a run whose child finished (result file exists)
    /// but the payload cannot be read back. Child exit 0 did the work —
    /// Warning (verify via journal/export folder); anything else Failed with
    /// the read reason. Pure: unit-covered by Stage 4 (chmod-000 simulates
    /// the root-owned-0600 trigger without privilege).</summary>
    public static ScriptRunResult MapUnreadableResult(string scriptId, int childExitCode, Exception readError)
    {
        return new ScriptRunResult
        {
            Outcome = childExitCode == 0 ? RunOutcome.Warning : RunOutcome.Failed,
            ScriptId = scriptId,
            Logs = new List<string>
            {
                "helper finished, but its result could not be read; check the journal and your export folder.",
                "read-back failed: " + readError.Message,
            },
        };
    }

    /// <summary>Log lines for a run whose child never wrote a result file
    /// (refusal, crash, transport failure): the header plus the child's own
    /// stderr, verbatim — that line names the reason (e.g. "Elevated child
    /// refused: …"). Pure: unit-covered by Stage 4.</summary>
    public static List<string> NoResultLogs(int childExitCode, IReadOnlyList<string> childStderrLines)
    {
        var logs = new List<string> { "The elevated helper did not write a result file." };
        foreach (var line in childStderrLines)
            if (!string.IsNullOrWhiteSpace(line))
                logs.Add(line.Trim());
        if (logs.Count == 1)
            logs.Add($"(elevated child exit code: {childExitCode})");
        return logs;
    }

    private static (int emitted, string carry) PumpLiveLog(string path, int emitted, string carry, Action<string> onLine)
    {
        if (!File.Exists(path)) return (emitted, carry);
        try
        {
            string all;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                all = sr.ReadToEnd();
            }
            string[] lines = all.Split('\n');
            int complete = lines.Length > 0 ? lines.Length - 1 : 0;
            for (int i = emitted; i < complete; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length > 0) onLine(line);
            }
            if (complete >= emitted) emitted = complete;
            return (emitted, lines.Length > 0 ? lines[^1] : carry);
        }
        catch
        {
            return (emitted, carry);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static ScriptRunResult ReadResultFile(string path, string scriptId)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var result = new ScriptRunResult
        {
            ScriptId = doc.TryGetProperty("ScriptId", out var idEl) ? idEl.GetString() ?? scriptId : scriptId,
            Outcome = doc.TryGetProperty("Outcome", out var oEl) ? OutcomeFromString(oEl.GetString()) : RunOutcome.Failed,
            Logs = new List<string>(),
            ItemCounts = new Dictionary<string, int>(),
        };
        if (doc.TryGetProperty("Logs", out var logsEl) && logsEl.ValueKind == JsonValueKind.Array)
            foreach (var l in logsEl.EnumerateArray())
                result.Logs.Add(l.GetString() ?? "");
        if (doc.TryGetProperty("ItemCounts", out var icEl) && icEl.ValueKind == JsonValueKind.Object)
            foreach (var p in icEl.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                    result.ItemCounts[p.Name] = n;
        return result;
    }

    private static string OutcomeToString(RunOutcome o) => o switch
    {
        RunOutcome.Success => "Success",
        RunOutcome.Warning => "Warning",
        RunOutcome.Failed => "Failed",
        RunOutcome.Cancelled => "Cancelled",
        _ => "Failed",
    };

    private static RunOutcome OutcomeFromString(string? s) => s switch
    {
        "Success" => RunOutcome.Success,
        "Warning" => RunOutcome.Warning,
        "Cancelled" => RunOutcome.Cancelled,
        _ => RunOutcome.Failed,
    };
}
