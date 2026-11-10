using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.App.Hosting;

/// <summary>
/// Schlanker Datei-Logger: eine Datei pro Tag unter <see cref="AppPaths.LogsDirectory"/>, Schreiben im Hintergrund,
/// Aufbewahrung 14 Tage.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly List<FileLoggerProvider> Instances = [];
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Thread _writer;
    private readonly string _directory;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information, int retentionDays = 14)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        Cleanup(retentionDays);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Musterdepot.FileLogger" };
        _writer.Start();
        lock (Instances) Instances.Add(this);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    internal void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private string CurrentFile => Path.Combine(_directory, $"musterdepot-{DateTime.Now:yyyyMMdd}.log");

    private void WriteLoop()
    {
        var batch = new StringBuilder();
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            batch.AppendLine(line);
            while (batch.Length < 64_000 && _queue.TryTake(out var more)) batch.AppendLine(more);
            try
            {
                File.AppendAllText(CurrentFile, batch.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Datei gesperrt: Zeilen verwerfen statt das Programm zu blockieren.
            }
            catch (UnauthorizedAccessException)
            {
            }
            batch.Clear();
        }
    }

    private void Cleanup(int retentionDays)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory, "musterdepot-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-retentionDays)) File.Delete(f);
        }
        catch (Exception)
        {
            // Aufräumen ist optional.
        }
    }

    /// <summary>Schreibt ausstehende Zeilen (beim Beenden).</summary>
    public static void FlushAll()
    {
        List<FileLoggerProvider> copy;
        lock (Instances) copy = [.. Instances];
        foreach (var p in copy) p.Dispose();
    }

    /// <summary>Notfall-Protokoll, falls der Host noch nicht steht.</summary>
    public static void WriteEmergency(string message, Exception? ex)
    {
        try
        {
            var dir = AppPaths.LogsDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"musterdepot-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:O} CRIT [Startup] {message}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        lock (Instances) Instances.Remove(this);
        if (_queue.IsAddingCompleted) return;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRCE",
                LogLevel.Debug => "DBUG",
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "FAIL",
                LogLevel.Critical => "CRIT",
                _ => "    ",
            };
            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
              .Append(' ').Append(level).Append(" [").Append(category).Append("] ")
              .Append(formatter(state, exception));
            if (exception is not null) sb.AppendLine().Append(exception);
            provider.Enqueue(sb.ToString());
        }
    }
}
