using System.Globalization;
using System.Text;

namespace Litos.SoftwareFactory.Host.Logging;

/// <summary>
/// The host's log, also written to a file a day in FACTORY_LOG_DIR (SoftwareFactoryInstallation.md
/// §9.8): a host started hidden by a service manager has no console, and its log was otherwise
/// lost. Files are named host-YYYYMMDD.log by UTC date, and those older than
/// <see cref="KeptDays"/> are removed when a new one starts. Lines are flushed as they are written,
/// so a crash loses nothing that was logged.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int KeptDays = 14;

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string directory, Func<DateTimeOffset>? now = null)
    {
        _directory = Path.GetFullPath(directory);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_directory);
    }

    public static string FileName(DateOnly day) => $"host-{day:yyyyMMdd}.log";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        var now = _now().ToUniversalTime();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(' ')
            .Append(Level(level)).Append(' ')
            .Append(category);
        if (eventId.Id != 0)
            line.Append('[').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
        line.Append(": ").Append(message);
        if (exception is not null)
            line.AppendLine().Append(exception);

        lock (_lock)
        {
            try
            {
                WriterFor(DateOnly.FromDateTime(now.UtcDateTime)).WriteLine(line.ToString());
            }
            catch (IOException)
            {
                // A log that cannot be written must not take the host down with it.
            }
        }
    }

    private StreamWriter WriterFor(DateOnly day)
    {
        if (_writer is not null && day == _day)
            return _writer;

        _writer?.Dispose();
        _day = day;
        var stream = new FileStream(Path.Combine(_directory, FileName(day)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        RemoveOldFiles(day);
        return _writer;
    }

    private void RemoveOldFiles(DateOnly today)
    {
        var oldest = today.AddDays(-KeptDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "host-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["host-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Removed on a later day instead.
                }
            }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
