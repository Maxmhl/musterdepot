using System.Net;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Gettex;

/// <summary>
/// gettex (Aktien, 15 Min verzögert): ruft alle 15 Minuten die CSV-Dateien der letzten 24 h ab, liest sie robust ein
/// und meldet Trades/Kurse mit <c>DelayedMinutes = 15</c> und <c>Quality = Delayed</c>. Symbol = ISIN.
/// Formatwechsel werden als <see cref="ConnectionStatusChanged"/> mit Meldung gemeldet, nie als Absturz.
/// </summary>
public sealed class GettexSource : MarketDataSourceBase, IHistoryCapabilities
{
    public const string SourceName = "gettex";

    private readonly GettexOptions _options;
    private readonly HttpClient _http;
    private readonly GettexCsvParser _parser;
    private readonly Lock _dataLock = new();
    private readonly HashSet<string> _processedFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GettexTrade>> _trades = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastEmitted = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Lock _runLock = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public GettexSource(GettexOptions options, HttpClient http, IClock clock, ILogger<GettexSource>? logger = null)
        : base(SourceName, clock, logger)
    {
        _options = options;
        _http = http;
        _parser = new GettexCsvParser(options.Parser);
    }

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Stock, AssetClass.Etf];
    public override bool IsRealtime => false;
    public override int DelayedMinutes => _options.DelayedMinutes;
    public GettexOptions Options => _options;
    /// <summary>Fingerabdruck des zuletzt erkannten Dateiformats.</summary>
    public string? CurrentFormatFingerprint => _parser.LastFingerprint;
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public TimeSpan? GetMaxHistoryLookback(Timeframe timeframe) => _options.Lookback;

    // ---------- Lebenszyklus ----------
    public override Task StartAsync(CancellationToken ct)
    {
        StartLoop(pollImmediately: true);
        return Task.CompletedTask;
    }

    private void StartLoop(bool pollImmediately)
    {
        lock (_runLock)
        {
            if (_runTask is { IsCompleted: false }) return;
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            if (State is ConnectionState.Disconnected or ConnectionState.Suspended) SetState(ConnectionState.Connecting);
            _runTask = Task.Run(() => RunAsync(pollImmediately, token), CancellationToken.None);
        }
    }

    private async Task RunAsync(bool pollImmediately, CancellationToken ct)
    {
        bool first = pollImmediately;
        while (!ct.IsCancellationRequested)
        {
            if (!first)
            {
                try { await DelayAsync(_options.PollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            first = false;
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "gettex-Abruf fehlgeschlagen");
                SetState(ConnectionState.Reconnecting, $"gettex-Abruf fehlgeschlagen: {ex.Message}");
            }
        }
    }

    private async Task StopLoopAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_runLock)
        {
            task = _runTask; cts = _runCts;
            _runTask = null; _runCts = null;
        }
        if (cts is null) return;
        cts.Cancel();
        if (task is not null) { try { await task.ConfigureAwait(false); } catch { /* beendet */ } }
        cts.Dispose();
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await StopLoopAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, "Gestoppt");
    }

    protected override Task DisconnectForSuspendAsync(CancellationToken ct) => StopLoopAsync();

    protected override async Task ReconnectAfterSuspendAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        // Erst nachladen (für das Lücken-Backfill), dann regulär weiter abfragen.
        try { await PollOnceAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { SetState(ConnectionState.Reconnecting, $"gettex-Abruf fehlgeschlagen: {ex.Message}"); }
        StartLoop(pollImmediately: false);
    }

    // ---------- Abruf ----------

    /// <summary>Dateinamen aller Zeitfenster im Rückblick, die bereits veröffentlicht sein sollten (älteste zuerst).</summary>
    public IReadOnlyList<(DateTimeOffset SlotStartUtc, string FileName)> GetExpectedFiles(DateTimeOffset nowUtc)
    {
        var interval = _options.FileInterval;
        var start = new DateTimeOffset((nowUtc - _options.Lookback).UtcTicks / interval.Ticks * interval.Ticks, TimeSpan.Zero);
        var list = new List<(DateTimeOffset, string)>();
        for (var slot = start; slot + interval + _options.PublicationDelay <= nowUtc; slot += interval)
        {
            var nameTime = _options.FileTimesAreLocal ? BerlinTime.FromUtc(slot) : slot.UtcDateTime;
            foreach (var p in _options.FileNamePatterns)
                list.Add((slot, GettexOptions.FormatFileName(p, nameTime)));
        }
        return list;
    }

    /// <summary>Ruft alle noch nicht verarbeiteten Dateien ab. Rückgabe: Anzahl neuer Trades.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Clock.UtcNow;
            var pending = GetExpectedFiles(now).Where(f => { lock (_dataLock) return !_processedFiles.Contains(f.FileName); })
                .Take(_options.MaxFilesPerPoll).ToList();
            int newTrades = 0, errors = 0, fetched = 0;
            string? lastError = null;
            foreach (var (slot, name) in pending)
            {
                ct.ThrowIfCancellationRequested();
                byte[]? content;
                try { content = await FetchAsync(name, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    lastError = ex.Message;
                    continue;
                }
                if (content is null)
                {
                    // Datei existiert nicht: bei alten Fenstern nicht erneut versuchen.
                    if (now - slot > TimeSpan.FromHours(2)) lock (_dataLock) _processedFiles.Add(name);
                    continue;
                }
                fetched++;
                var fileDate = DateOnly.FromDateTime(_options.FileTimesAreLocal ? BerlinTime.FromUtc(slot) : slot.UtcDateTime);
                newTrades += IngestFile(content, fileDate, name);
            }
            Prune(now);
            if (errors > 0 && fetched == 0)
                SetState(ConnectionState.Reconnecting, $"gettex nicht erreichbar ({errors} Fehler): {lastError}");
            else if (State != ConnectionState.Connected)
                SetState(ConnectionState.Connected, LastStatusMessage is { } m && m.StartsWith("gettex: Dateiformat", StringComparison.Ordinal) ? m : null);
            return newTrades;
        }
        finally { _pollGate.Release(); }
    }

    private async Task<byte[]?> FetchAsync(string fileName, CancellationToken ct)
    {
        if (_options.LocalDirectory is { } dir)
        {
            var path = Path.Combine(dir, fileName);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
        }
        var url = _options.BaseUrl.TrimEnd('/') + "/" + fileName;
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Liest den Inhalt einer Datei ein, speichert die Trades und meldet neue Trades abonnierter ISINs.</summary>
    public int IngestFile(byte[] content, DateOnly? fileDate, string fileName)
    {
        GettexParseResult result;
        lock (_dataLock)
        {
            if (!_processedFiles.Add(fileName)) return 0;
            result = _parser.Parse(new MemoryStream(content), fileDate);
        }
        if (result.FormatChanged)
            PublishWarning($"gettex: Dateiformat geändert in {fileName} (vorher [{result.PreviousFingerprint}], jetzt [{result.Fingerprint}])");
        if (!result.FormatRecognized)
        {
            PublishWarning($"gettex: Format von {fileName} nicht erkannt ({string.Join("; ", result.Warnings.Take(3))})");
            return 0;
        }
        if (result.SkippedLines > 0)
            Logger.LogInformation("gettex {File}: {Skipped} Zeilen übersprungen", fileName, result.SkippedLines);
        return Ingest(result.Trades);
    }

    private int Ingest(IReadOnlyList<GettexTrade> trades)
    {
        var now = Clock.UtcNow;
        var events = new List<MarketEvent>();
        lock (_dataLock)
        {
            foreach (var g in trades.GroupBy(t => t.Isin))
            {
                if (!_trades.TryGetValue(g.Key, out var list)) _trades[g.Key] = list = [];
                list.AddRange(g);
                list.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
            }
        }
        foreach (var g in trades.GroupBy(t => t.Isin))
        {
            if (!IsSubscribed(g.Key)) continue;
            DateTimeOffset last;
            lock (_dataLock) last = _lastEmitted.GetValueOrDefault(g.Key, DateTimeOffset.MinValue);
            var fresh = g.Where(t => t.TimeUtc > last).OrderBy(t => t.TimeUtc).ToList();
            if (fresh.Count == 0) continue;
            foreach (var t in fresh) events.Add(ToTradeTick(t, now));
            lock (_dataLock) _lastEmitted[g.Key] = fresh[^1].TimeUtc;
            if (BuildQuote(g.Key, now) is { } q) events.Add(q);
        }
        foreach (var e in events.OrderBy(e => e.AsOfUtc).ThenBy(e => e is QuoteTick ? 1 : 0)) Publish(e);
        return trades.Count;
    }

    private TradeTick ToTradeTick(GettexTrade t, DateTimeOffset now) => new()
    {
        Symbol = t.Isin,
        Source = SourceName,
        AsOfUtc = t.TimeUtc,
        ReceivedUtc = now,
        DelayedMinutes = DelayedMinutes,
        Quality = DataQuality.Delayed,
        Price = t.Price,
        Quantity = t.Volume,
    };

    private QuoteTick? BuildQuote(string isin, DateTimeOffset now)
    {
        List<GettexTrade> list;
        lock (_dataLock)
        {
            if (!_trades.TryGetValue(isin, out var l) || l.Count == 0) return null;
            list = [.. l];
        }
        var last = list[^1];
        var withBook = list.LastOrDefault(t => t.Bid is not null || t.Ask is not null);
        // Änderung ggü. letztem Kurs vor dem aktuellen Handelstag (Berliner Zeit).
        var dayStartUtc = BerlinTime.ToUtc(BerlinTime.FromUtc(last.TimeUtc).Date);
        var prevClose = list.LastOrDefault(t => t.TimeUtc < dayStartUtc);
        decimal? change = prevClose is { Price: > 0 } pc ? Math.Round((last.Price - pc.Price) / pc.Price * 100m, 4) : null;
        var dayVolume = list.Where(t => t.TimeUtc >= dayStartUtc).Sum(t => t.Volume);
        return new QuoteTick
        {
            Symbol = isin,
            Source = SourceName,
            AsOfUtc = last.TimeUtc,
            ReceivedUtc = now,
            DelayedMinutes = DelayedMinutes,
            Quality = DataQuality.Delayed,
            Last = last.Price,
            Bid = withBook?.Bid,
            Ask = withBook?.Ask,
            BidSize = withBook?.BidSize,
            AskSize = withBook?.AskSize,
            Volume24h = dayVolume,
            ChangePercent = change,
        };
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _options.Lookback - TimeSpan.FromHours(1);
        lock (_dataLock)
            foreach (var list in _trades.Values)
                list.RemoveAll(t => t.TimeUtc < cutoff);
    }

    // ---------- Abfragen ----------
    public override bool Supports(string symbol) => SymbolHelper.IsIsin(symbol);

    protected override Task OnSubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        // Vorhandenen Stand sofort melden (Watchlist zeigt dann gleich Kurs + "verzögert").
        var now = Clock.UtcNow;
        foreach (var s in symbols)
            if (BuildQuote(s, now) is { } q)
            {
                lock (_dataLock) _lastEmitted[s] = q.AsOfUtc;
                Publish(q);
            }
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        List<string> seen;
        lock (_dataLock) seen = _trades.Keys.ToList();
        var all = GettexInstruments.Dax.Concat(seen.Where(i => GettexInstruments.Find(i) is null).Select(i => GettexInstruments.ForIsin(i)));
        return Task.FromResult(FilterInstruments(all, query));
    }

    public override Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        List<GettexTrade> list;
        lock (_dataLock) list = _trades.TryGetValue(symbol, out var l) ? [.. l] : [];
        var from = fromUtc is { } f ? timeframe.Floor(f) : DateTimeOffset.MinValue;
        var to = toUtc ?? DateTimeOffset.MaxValue;
        IReadOnlyList<Candle> candles = TradeCandleAggregator.FromTrades(symbol, SourceName, timeframe,
                list.Where(t => t.TimeUtc >= from && t.TimeUtc < to).Select(t => (t.TimeUtc, t.Price, t.Volume)),
                Clock.UtcNow - TimeSpan.FromMinutes(DelayedMinutes));
        return Task.FromResult(candles);
    }

    public override Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
        => Task.FromResult<OrderBookSnapshot?>(null);
}
