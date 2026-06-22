using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PhotoAlbum.App.Services;

/// <summary>
/// Writes albumlog.md — a human-readable per-run trace log.
/// Each new application run overwrites the previous log.
/// All writes are synchronous and lock-protected so crash paths never lose entries.
/// </summary>
public static class RunLogger
{
    // ── State ──────────────────────────────────────────────────────────────

    private static string? _path;
    private static readonly object _lock = new();
    private static int _seq;
    private static readonly DateTime _start = DateTime.Now;
    private static readonly string _sessionId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private static bool _hasCrash;
    private static bool _initialized;

    // ── Initialization ─────────────────────────────────────────────────────

    public static void Initialize(string appDataDir)
    {
        _path = Path.Combine(appDataDir, "albumlog.md");

        var proc   = Process.GetCurrentProcess();
        var entry  = Assembly.GetEntryAssembly();
        var osDesc = RuntimeInformation.OSDescription;
        var rt     = RuntimeInformation.FrameworkDescription;

        var header = new StringBuilder();
        header.AppendLine("# PhotoAlbum Run Log");
        header.AppendLine();
        header.AppendLine("| Property | Value |");
        header.AppendLine("|---|---|");
        header.AppendLine($"| **Session Start** | {_start:yyyy-MM-dd HH:mm:ss.fff} |");
        header.AppendLine($"| **Session ID** | `{_sessionId}` |");
        header.AppendLine($"| **PID** | {proc.Id} |");
        header.AppendLine($"| **OS** | {osDesc} |");
        header.AppendLine($"| **Runtime** | {rt} |");
        header.AppendLine($"| **Machine** | {Environment.MachineName} |");
        header.AppendLine($"| **User** | {Environment.UserName} |");
        header.AppendLine($"| **Executable** | `{entry?.Location ?? "unknown"}` |");
        header.AppendLine($"| **Working Dir** | `{Environment.CurrentDirectory}` |");
        header.AppendLine();
        header.AppendLine("---");
        header.AppendLine();
        header.AppendLine("## Action Log");
        header.AppendLine();
        header.AppendLine("| # | Time | Level | Source | Message | Detail |");
        header.AppendLine("|---|------|-------|--------|---------|--------|");

        File.WriteAllText(_path, header.ToString(), Encoding.UTF8);
        _initialized = true;

        Append(BuildRow("START", "App", "Session opened — logger initialized", null));
        Info("App", $"Log path: {_path}");
        Info("App", $"OS: {osDesc}  Runtime: {rt}  PID: {proc.Id}");
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Log a user-initiated UI action (click, key press, navigation).</summary>
    public static void Action(string source, string action, string? detail = null)
        => Append(BuildRow("ACTION", source, action, detail));

    /// <summary>Log an informational lifecycle event (service start, DB open, etc.).</summary>
    public static void Info(string source, string message, string? detail = null)
        => Append(BuildRow("INFO", source, message, detail));

    /// <summary>Log a recoverable warning.</summary>
    public static void Warn(string source, string message, Exception? ex = null)
        => Append(BuildRow("WARN", source, message, ex is null ? null : SafeMessage(ex)));

    /// <summary>Log a handled error — does NOT terminate the session section.</summary>
    public static void Error(string source, string message, Exception ex)
    {
        Append(BuildRow("ERROR", source, message, SafeMessage(ex)));
        AppendRaw(BuildExceptionBlock(ex, source, "ERROR"));
    }

    /// <summary>
    /// Log a fatal crash with full exception chain, stack trace, and process state.
    /// Flushes synchronously — safe to call immediately before process exit.
    /// </summary>
    public static void CrashDump(Exception ex, string context)
    {
        _hasCrash = true;
        Append(BuildRow("FATAL", context, "⚠ UNHANDLED EXCEPTION — see crash dump below", SafeMessage(ex)));
        AppendRaw(BuildCrashDump(ex, context));
    }

    /// <summary>Writes the closing footer. Called on clean exit and on crash exit.</summary>
    public static void Close(int exitCode = 0)
    {
        var elapsed = DateTime.Now - _start;
        var status  = _hasCrash ? "CRASHED" : exitCode == 0 ? "CLEAN" : $"EXIT({exitCode})";
        Append(BuildRow("END", "App",
            $"Session closed — {status}",
            $"Uptime: {elapsed:hh\\:mm\\:ss\\.fff}  Actions: {_seq}"));

        AppendRaw($"\n---\n\n*Log closed: {DateTime.Now:HH:mm:ss.fff}  |  " +
                  $"Status: {status}  |  Session: {_sessionId}*\n");
    }

    // ── Internal builders ──────────────────────────────────────────────────

    private static string BuildRow(string level, string source, string message, string? detail)
    {
        var n    = System.Threading.Interlocked.Increment(ref _seq);
        var time = DateTime.Now.ToString("HH:mm:ss.fff");
        var msg  = Escape(message);
        var det  = string.IsNullOrWhiteSpace(detail) ? "" : Escape(detail!);
        return $"| {n} | {time} | **{level}** | {Escape(source)} | {msg} | {det} |\n";
    }

    private static string BuildExceptionBlock(Exception ex, string context, string severity)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"### {severity} — Exception Detail (`{context}`)");
        sb.AppendLine();
        AppendExceptionChain(sb, ex, 1);
        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildCrashDump(Exception ex, string context)
    {
        var proc = Process.GetCurrentProcess();
        var sb   = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## ⚠ FATAL CRASH DUMP");
        sb.AppendLine();
        sb.AppendLine($"**Timestamp:** {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  ");
        sb.AppendLine($"**Session ID:** `{_sessionId}`  ");
        sb.AppendLine($"**Context:** `{context}`  ");
        sb.AppendLine($"**Thread:** ManagedId={System.Threading.Thread.CurrentThread.ManagedThreadId}  " +
                      $"IsBackground={System.Threading.Thread.CurrentThread.IsBackground}  " +
                      $"IsThreadPoolThread={System.Threading.Thread.CurrentThread.IsThreadPoolThread}  ");
        sb.AppendLine();

        // Exception chain
        sb.AppendLine("### Exception Chain");
        sb.AppendLine();
        AppendExceptionChain(sb, ex, 1);

        // Process state
        sb.AppendLine();
        sb.AppendLine("### Process State at Crash");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|---|---|");
        try
        {
            proc.Refresh();
            sb.AppendLine($"| Working Set | {proc.WorkingSet64 / 1_048_576.0:F1} MB |");
            sb.AppendLine($"| Private Memory | {proc.PrivateMemorySize64 / 1_048_576.0:F1} MB |");
            sb.AppendLine($"| Virtual Memory | {proc.VirtualMemorySize64 / 1_048_576.0:F1} MB |");
            sb.AppendLine($"| Thread Count | {proc.Threads.Count} |");
            sb.AppendLine($"| Handle Count | {proc.HandleCount} |");
            sb.AppendLine($"| CPU Time Total | {proc.TotalProcessorTime:hh\\:mm\\:ss\\.fff} |");
            sb.AppendLine($"| Uptime | {(DateTime.Now - _start):hh\\:mm\\:ss\\.fff} |");
        }
        catch (Exception stateEx)
        {
            sb.AppendLine($"| (process state read failed) | {SafeMessage(stateEx)} |");
        }

        // GC state
        try
        {
            sb.AppendLine($"| GC Gen0 Collections | {GC.CollectionCount(0)} |");
            sb.AppendLine($"| GC Gen1 Collections | {GC.CollectionCount(1)} |");
            sb.AppendLine($"| GC Gen2 Collections | {GC.CollectionCount(2)} |");
            sb.AppendLine($"| GC Total Allocated | {GC.GetTotalAllocatedBytes(precise: false) / 1_048_576.0:F1} MB |");
            var mem = GC.GetGCMemoryInfo();
            sb.AppendLine($"| GC Heap Size | {mem.HeapSizeBytes / 1_048_576.0:F1} MB |");
            sb.AppendLine($"| GC Fragmented | {mem.FragmentedBytes / 1_048_576.0:F1} MB |");
        }
        catch { /* non-critical */ }

        // Loaded assemblies
        sb.AppendLine();
        sb.AppendLine("### Loaded Assemblies");
        sb.AppendLine();
        sb.AppendLine("| Assembly | Version | Location |");
        sb.AppendLine("|---|---|---|");
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()
                         .Where(a => !a.IsDynamic)
                         .OrderBy(a => a.GetName().Name))
            {
                var name    = Escape(asm.GetName().Name ?? "?");
                var version = asm.GetName().Version?.ToString() ?? "?";
                var loc     = Escape(asm.Location);
                sb.AppendLine($"| {name} | {version} | `{loc}` |");
            }
        }
        catch (Exception asmEx)
        {
            sb.AppendLine($"| (assembly list failed) | | {SafeMessage(asmEx)} |");
        }

        // Environment
        sb.AppendLine();
        sb.AppendLine("### Environment Variables (selected)");
        sb.AppendLine();
        sb.AppendLine("| Key | Value |");
        sb.AppendLine("|---|---|");
        foreach (var key in new[] { "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "PATH" })
        {
            var val = Environment.GetEnvironmentVariable(key) ?? "(not set)";
            if (val.Length > 120) val = val[..120] + "…";
            sb.AppendLine($"| `{key}` | `{Escape(val)}` |");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendExceptionChain(StringBuilder sb, Exception ex, int depth)
    {
        var idx = 1;
        var current = ex;
        while (current is not null)
        {
            sb.AppendLine($"#### [{idx}] `{current.GetType().FullName}`");
            sb.AppendLine();
            sb.AppendLine($"**Message:** {Escape(current.Message)}  ");
            sb.AppendLine($"**HResult:** 0x{(uint)current.HResult:X8}  ");
            if (current.Source is not null)
                sb.AppendLine($"**Source:** {Escape(current.Source)}  ");
            if (current.TargetSite is not null)
                sb.AppendLine($"**TargetSite:** `{Escape(current.TargetSite.ToString() ?? "?")}` ");
            sb.AppendLine();
            sb.AppendLine("**Stack Trace:**");
            sb.AppendLine("```");
            sb.AppendLine(string.IsNullOrWhiteSpace(current.StackTrace)
                ? "(no stack trace available)"
                : current.StackTrace.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();

            // AggregateException: list all inner exceptions
            if (current is AggregateException agg && agg.InnerExceptions.Count > 1)
            {
                sb.AppendLine($"**Aggregate — {agg.InnerExceptions.Count} inner exceptions:**");
                sb.AppendLine();
                for (var i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    sb.AppendLine($"##### Aggregate[{i}]");
                    AppendExceptionChain(sb, agg.InnerExceptions[i], depth + 1);
                }
                break; // InnerException is just [0] again — already covered
            }

            current = current.InnerException;
            idx++;
        }
    }

    // ── File I/O helpers ───────────────────────────────────────────────────

    private static void Append(string line)
    {
        if (!_initialized || _path is null) return;
        lock (_lock)
        {
            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }

    private static void AppendRaw(string block)
    {
        if (!_initialized || _path is null) return;
        lock (_lock)
        {
            File.AppendAllText(_path, block, Encoding.UTF8);
        }
    }

    // ── String helpers ─────────────────────────────────────────────────────

    private static string Escape(string? s)
    {
        if (s is null) return "";
        return s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
    }

    private static string SafeMessage(Exception ex)
    {
        try { return $"{ex.GetType().Name}: {ex.Message}"; }
        catch { return "(exception message unavailable)"; }
    }
}
