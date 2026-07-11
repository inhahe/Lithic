using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LithicBackup.Infrastructure.Data;

namespace LithicBackup.Infrastructure.Diagnostics;

/// <summary>
/// Process-wide crash and diagnostic logger shared by the WPF GUI and the
/// Windows Service. Captures unhandled exceptions (and unobserved task
/// exceptions) that would otherwise terminate the process silently, writing a
/// full stack trace plus environment context to a durable file under
/// <c>C:\ProgramData\LithicBackup\logs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of output are produced:
/// </para>
/// <list type="bullet">
///   <item><description>
///     A rolling daily diagnostic log (<c>lithic-{component}-{yyyyMMdd}.log</c>)
///     that every call appends to.
///   </description></item>
///   <item><description>
///     A standalone crash report (<c>crash-{component}-{timestamp}.log</c>) for
///     each fatal/unhandled exception, so a crash is never buried inside the
///     rolling log and is trivial to attach to a bug report.
///   </description></item>
/// </list>
/// <para>
/// Everything is best-effort and wrapped in try/catch: a logger must never be
/// the thing that crashes the process. It depends only on <c>System.*</c> so it
/// can live in Infrastructure without pulling in a logging package.
/// </para>
/// </remarks>
public static class CrashLogger
{
    private static readonly object _gate = new();
    private static string _component = "app";
    private static bool _initialized;

    /// <summary>
    /// Hook the process-global unhandled-exception events and record startup.
    /// Safe to call more than once; only the first call installs handlers.
    /// </summary>
    /// <param name="component">
    /// Short tag distinguishing the producer in filenames/log lines
    /// (e.g. "gui" or "worker").
    /// </param>
    public static void Initialize(string component)
    {
        lock (_gate)
        {
            if (_initialized)
                return;
            _initialized = true;
            _component = string.IsNullOrWhiteSpace(component) ? "app" : component.Trim();
        }

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                LogFatal(ex, $"AppDomain.UnhandledException (terminating={e.IsTerminating})");
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogFatal(e.Exception, "TaskScheduler.UnobservedTaskException");
                // Mark observed so the unobserved-exception policy doesn't escalate.
                e.SetObserved();
            };

            Log(null, $"CrashLogger initialized for '{_component}'.");
        }
        catch
        {
            // Never let diagnostics installation take down startup.
        }
    }

    /// <summary>
    /// Record a non-fatal diagnostic entry (optionally with an exception) to the
    /// rolling daily log.
    /// </summary>
    public static void Log(Exception? ex, string context)
    {
        try
        {
            var entry = BuildEntry("INFO", context, ex);
            AppendToDaily(entry);
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Record a fatal/unhandled exception to both the rolling daily log and a
    /// dedicated, timestamped crash-report file.
    /// </summary>
    public static void LogFatal(Exception? ex, string context)
    {
        try
        {
            var entry = BuildEntry("FATAL", context, ex);
            AppendToDaily(entry);
            WriteCrashReport(entry);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string BuildEntry(string level, string context, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ");
        sb.Append(level).Append(" [").Append(_component).Append("] ");
        sb.AppendLine(context);

        // Environment context — captured once per entry so a crash report is
        // self-contained without needing the rolling log for surrounding lines.
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            sb.Append("  Process : ").Append(Process.GetCurrentProcess().ProcessName)
              .Append(" (PID ").Append(Environment.ProcessId).AppendLine(")");
            sb.Append("  Assembly: ").AppendLine(asm.GetName().FullName);
            sb.Append("  User    : ").Append(Environment.UserDomainName).Append('\\').AppendLine(Environment.UserName);
            sb.Append("  Machine : ").AppendLine(Environment.MachineName);
            sb.Append("  OS      : ").AppendLine(RuntimeInformation.OSDescription);
            sb.Append("  CLR     : ").AppendLine(RuntimeInformation.FrameworkDescription);
        }
        catch
        {
            // Context is a nicety; never block the actual exception text.
        }

        if (ex != null)
        {
            sb.AppendLine("  Exception:");
            sb.AppendLine(Indent(ex.ToString(), "    "));
        }

        sb.AppendLine(new string('-', 72));
        return sb.ToString();
    }

    private static string Indent(string text, string prefix)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(l => prefix + l));
    }

    private static void AppendToDaily(string entry)
    {
        var dir = ResolveLogsDir();
        var path = Path.Combine(dir, $"lithic-{_component}-{DateTime.Now:yyyyMMdd}.log");
        lock (_gate)
        {
            File.AppendAllText(path, entry, Encoding.UTF8);
        }
    }

    private static void WriteCrashReport(string entry)
    {
        var dir = ResolveLogsDir();
        var path = Path.Combine(dir, $"crash-{_component}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        File.WriteAllText(path, entry, Encoding.UTF8);
    }

    /// <summary>
    /// Resolve the shared logs directory, falling back to per-user and temp
    /// locations if the shared path is unavailable (e.g. ACL or disk issues).
    /// </summary>
    private static string ResolveLogsDir()
    {
        try
        {
            return CatalogLocation.LogsDirectory();
        }
        catch
        {
            // Fall through to local fallbacks.
        }

        try
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LithicBackup", "logs");
            Directory.CreateDirectory(local);
            return local;
        }
        catch
        {
            // Last resort: temp.
        }

        var temp = Path.Combine(Path.GetTempPath(), "LithicBackup-logs");
        Directory.CreateDirectory(temp);
        return temp;
    }
}
