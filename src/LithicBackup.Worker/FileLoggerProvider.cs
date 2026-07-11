using System.Collections.Concurrent;
using System.Text;
using LithicBackup.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace LithicBackup.Worker;

/// <summary>
/// Minimal file-based <see cref="ILoggerProvider"/> that mirrors the worker's
/// <see cref="ILogger"/> output to a rolling daily file under the shared logs
/// directory (<c>C:\ProgramData\LithicBackup\logs</c>).
/// </summary>
/// <remarks>
/// When running as a Windows Service the default logging goes to the Windows
/// Event Log, which is awkward to inspect and easy to overlook. This provider
/// gives the service the same kind of durable, human-readable diagnostic trail
/// the GUI gets via <c>CrashLogger</c>, so a hang or crash mid-backup leaves
/// evidence behind. It is intentionally tiny and dependency-free beyond the
/// logging abstractions already referenced by the worker.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _component;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string component, LogLevel minLevel = LogLevel.Information)
    {
        _component = string.IsNullOrWhiteSpace(component) ? "worker" : component.Trim();
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    private void Write(string categoryName, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ");
            sb.Append(level.ToString().ToUpperInvariant()).Append(' ');
            sb.Append('[').Append(categoryName).Append("] ");
            sb.AppendLine(message);
            if (exception != null)
                sb.AppendLine(exception.ToString());

            var dir = ResolveLogsDir();
            var path = Path.Combine(dir, $"lithic-{_component}-{DateTime.Now:yyyyMMdd}.log");
            lock (_gate)
            {
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never throw into the service's hot path.
        }
    }

    private static string ResolveLogsDir()
    {
        try
        {
            return CatalogLocation.LogsDirectory();
        }
        catch
        {
            var temp = Path.Combine(Path.GetTempPath(), "LithicBackup-logs");
            Directory.CreateDirectory(temp);
            return temp;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _provider._minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = formatter(state, exception);
            _provider.Write(_category, logLevel, eventId, message, exception);
        }
    }
}
