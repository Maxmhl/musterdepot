using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.Recorder.Cli;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.Recorder.Fixtures;

public sealed record FixtureScenario(string Name, IReadOnlyList<MarketEvent> Events, OrderScript Script);

/// <summary>
/// Erzeugt die Replay-Szenarien unter tests/data/replays deterministisch (fester Seed). Die Börsen-Hosts waren beim
/// Erstellen aus der Build-Umgebung nicht erreichbar, daher synthetische, aber in sich stimmige Daten
/// (Buch ↔ Quotes ↔ Trades ↔ Kerzen). Echte Aufzeichnungen können später mit <c>record</c> + <c>slice</c> ergänzt werden.
/// </summary>
public static class FixtureGenerator
{
    public const int DefaultSeed = 20261109;
    public const string QuietName = "btc-eur-quiet";
    public const string GapName = "btc-eur-gap";
    public const string DelayedName = "sap-delayed";
    private const string AiClient = "claude-code";

    public static Instrument BtcEur { get; } = new()
    {
        Symbol = "BTC/EUR", Name = "Bitcoin / Euro", AssetClass = AssetClass.Crypto, QuoteCurrency = "EUR",
        BaseAsset = "BTC", TickSize = 0.1m, QuantityStep = 0.00000001m, MinQuantity = 0.00005m,
        PrimarySource = "kraken", SourceSymbol = "BTC/EUR",
    };

    public static Instrument Sap { get; } = new()
    {
        Symbol = "DE0007164600", Name = "SAP SE", AssetClass = AssetClass.Stock, QuoteCurrency = "EUR",
        BaseAsset = "DE0007164600", Isin = "DE0007164600", Wkn = "716460", TickSize = 0.01m, QuantityStep = 1m, MinQuantity = 1m,
        PrimarySource = "gettex", SourceSymbol = "DE0007164600",
    };

    private static OrderScriptAccount MainAccount => new()
    {
        AccountId = "main", Name = "Testdepot", InitialCash = 100_000m,
        CryptoFees = FeeModelConfig.KrakenLike, StockFees = FeeModelConfig.NeoBroker,
    };

    public static IReadOnlyList<FixtureScenario> CreateAll(int seed = DefaultSeed) =>
        [CreateQuiet(seed), CreateGap(seed + 1), CreateDelayed(seed + 2)];

    public static async Task<int> RunAsync(CliArgs args, TextWriter output, CancellationToken ct)
    {
        args.EnsureOnly("out", "seed");
        var outDir = args.Get("out") ?? DefaultOutputDirectory()
            ?? throw new CliException("Repository-Wurzel (Musterdepot.slnx) nicht gefunden – bitte --out angeben.");
        var seed = args.GetInt("seed", DefaultSeed);
        foreach (var (scenario, replayPath, scriptPath) in await WriteAllAsync(outDir, seed, ct))
            output.WriteLine($"{scenario.Name,-16} {scenario.Events.Count,6:N0} Ereignisse, {scenario.Script.Steps.Count,2} Schritte → {replayPath}, {Path.GetFileName(scriptPath)}");
        return 0;
    }

    public static async Task<IReadOnlyList<(FixtureScenario Scenario, string ReplayPath, string ScriptPath)>> WriteAllAsync(
        string outDir, int seed = DefaultSeed, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        var result = new List<(FixtureScenario, string, string)>();
        foreach (var scenario in CreateAll(seed))
        {
            var errors = OrderScriptValidator.Validate(scenario.Script, scenario.Events);
            if (errors.Count > 0)
                throw new InvalidOperationException($"Szenario {scenario.Name} ist ungültig:\n" + string.Join("\n", errors));
            var replayPath = Path.Combine(outDir, scenario.Script.Replay);
            var scriptPath = Path.Combine(outDir, scenario.Name + ".orders.json");
            await MarketEventJsonl.WriteAsync(replayPath, scenario.Events, ct);
            await OrderScriptJson.SaveAsync(scriptPath, scenario.Script, ct);
            result.Add((scenario, replayPath, scriptPath));
        }
        return result;
    }

    private static string? DefaultOutputDirectory()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Musterdepot.slnx")))
                return Path.Combine(dir.FullName, "tests", "data", "replays");
        return null;
    }

    // ------------------------------------------------------------------ (a) ruhiger Markt
    public static FixtureScenario CreateQuiet(int seed)
    {
        var start = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var sim = new CryptoMarketSim(seed, BtcEur.Symbol, "kraken", 89_412.30m, 81_234_567);
        sim.Status(start, ConnectionState.Connected, "Kraken WS v2 verbunden");
        sim.StartCandle(start);
        for (int s = 0; s < 30 * 60; s++)
        {
            var t = start.AddSeconds(s);
            if (s > 0 && s % 60 == 0) sim.CloseCandle(t.AddMilliseconds(50));
            if (s % 300 == 0) sim.Book(t.AddMilliseconds(70), snapshot: true);
            sim.Second(t, s, 1.2m);
        }
        sim.CloseCandle(start.AddMinutes(30).AddMilliseconds(50));
        var events = sim.Events;

        DateTimeOffset At(int minute) => start.AddMinutes(minute);
        decimal P(int minute) => LastTradeBefore(events, BtcEur.Symbol, At(minute));
        var limitBuy = CryptoMarketSim.RoundTick(P(3) - 5m);
        var limitSell = CryptoMarketSim.RoundTick(P(5) + 4m);
        var stop = CryptoMarketSim.RoundTick(P(12) + 20m);
        var stopLimit = CryptoMarketSim.RoundTick(P(12) + 25m);
        var steps = new List<OrderScriptStep>
        {
            Place(At(2), Order("q-mkt-buy-1", OrderSide.Buy, OrderType.Market, qty: 0.05m),
                expect: new() { Accepted = true, FinalStatus = OrderStatus.Filled },
                note: "Market-Kauf läuft Stufe für Stufe durch das Orderbuch."),
            Place(At(3), Order("q-lmt-buy-1", OrderSide.Buy, OrderType.Limit, qty: 0.1m, limit: limitBuy, tif: TimeInForce.Gtc),
                expect: new() { Accepted = true, MaxFillPrice = limitBuy },
                note: "Ruhendes Limit 5 EUR unter Markt: füllt nur, wenn ein Trade durch das Limit geht (max. 25 % des Volumens)."),
            Place(At(5), Order("q-lmt-sell-1", OrderSide.Sell, OrderType.Limit, qty: 0.02m, limit: limitSell, tif: TimeInForce.Gtc, tag: "mean-revert"),
                CommandSource.Ai, AiClient, new() { Accepted = true, MinFillPrice = limitSell }),
            Place(At(10), Order("q-notional-1", OrderSide.Buy, OrderType.Market, notional: 1_000m),
                CommandSource.Ai, AiClient, new() { Accepted = true, FinalStatus = OrderStatus.Filled },
                "Betrag statt Stückzahl."),
            Place(At(12), Order("q-stoplimit-1", OrderSide.Buy, OrderType.StopLimit, qty: 0.03m, limit: stopLimit, stop: stop, tif: TimeInForce.Gtc),
                expect: new() { Accepted = true, MaxFillPrice = stopLimit }),
            Cancel(At(20), "q-stoplimit-1", note: "Storniert, falls noch offen; sonst muss die Stornierung sauber abgelehnt werden."),
            Place(At(25), Order("q-mkt-sell-1", OrderSide.Sell, OrderType.Market, qty: 0.05m),
                expect: new() { Accepted = true, FinalStatus = OrderStatus.Filled }),
            Place(At(26), Order("q-band-reject", OrderSide.Sell, OrderType.Limit, qty: 0.01m, limit: CryptoMarketSim.RoundTick(P(26) * 0.5m), tif: TimeInForce.Gtc),
                expect: new() { Accepted = false },
                note: "Tippfehler: Limit 50 % unter Markt verletzt das Preisband (10 %)."),
            Place(At(27), Order("q-maxorder-reject", OrderSide.Buy, OrderType.Market, qty: 0.5m),
                CommandSource.Ai, AiClient, new() { Accepted = false },
                "≈ 44.700 EUR > MaxOrderNotional 10.000 EUR: Risiko-Gate muss ablehnen."),
        };

        return new FixtureScenario(QuietName, events, new OrderScript
        {
            Scenario = QuietName,
            Description = "BTC/EUR, ruhiger Markt, 30 Minuten Kraken-Echtzeit: Orderbuch-Snapshots (alle 5 Min) und -Deltas, Trades, Quotes, M1-Kerzen. Market/Limit/Stop-Limit/Notional, Stornierung, zwei Ablehnungen durch das Risiko-Gate.",
            Replay = QuietName + ".jsonl",
            Seed = seed,
            Instruments = [BtcEur],
            Accounts = [MainAccount],
            Steps = steps,
        });
    }

    // ------------------------------------------------------------------ (b) Lücke + Kurssprung
    public static FixtureScenario CreateGap(int seed)
    {
        var start = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var sim = new CryptoMarketSim(seed, BtcEur.Symbol, "kraken", 89_380.50m, 81_400_000);
        sim.Status(start, ConnectionState.Connected, "Kraken WS v2 verbunden");
        sim.StartCandle(start);
        for (int s = 0; s < 8 * 60; s++)
        {
            var t = start.AddSeconds(s);
            if (s > 0 && s % 60 == 0) sim.CloseCandle(t.AddMilliseconds(50));
            if (s % 300 == 0) sim.Book(t.AddMilliseconds(70), snapshot: true);
            sim.Second(t, s, 1.2m);
        }

        // 10:08 Standby. Während der Lücke fällt der Kurs um ~1.800 EUR und erholt sich leicht.
        var gapStart = start.AddMinutes(8);
        var gapEnd = start.AddMinutes(15);
        sim.CloseCandle(gapStart.AddMilliseconds(50));
        sim.Status(gapStart.AddMilliseconds(100), ConnectionState.Suspended, "Standby – Verbindung sauber getrennt");
        var backfill = sim.SimulateBackfill(gapStart, [-420m, -460m, -380m, -300m, -260m, 90m, 70m], 18m);

        var reconnect = gapEnd.AddSeconds(20);
        sim.Status(reconnect, ConnectionState.Reconnecting, "Aufwachen aus Standby");
        sim.Status(reconnect.AddMilliseconds(1400), ConnectionState.Connected, "Kraken WS v2 verbunden");
        sim.Gap(reconnect.AddMilliseconds(1500), gapStart, gapEnd, backfill);
        sim.Book(reconnect.AddMilliseconds(1600), snapshot: true);
        sim.StartCandle(gapEnd);

        const int jumpSecond = 18 * 60 + 30;
        for (int s = 15 * 60 + 22; s < 25 * 60; s++)
        {
            var t = start.AddSeconds(s);
            if (s % 60 == 0) sim.CloseCandle(t.AddMilliseconds(50));
            if (s == jumpSecond)
            {
                // Kurssprung im laufenden Handel: Buch und Trades springen 420 EUR tiefer.
                sim.Jump(-420m);
                sim.Book(t.AddMilliseconds(100), snapshot: false);
                sim.Trade(t.AddMilliseconds(200), OrderSide.Sell);
                sim.Trade(t.AddMilliseconds(260), OrderSide.Sell);
                sim.Second(t, s, 1.2m, firstTradeOffsetMs: 330);
            }
            else sim.Second(t, s, 1.2m);
        }
        sim.CloseCandle(start.AddMinutes(25).AddMilliseconds(50));
        var events = sim.Events;

        DateTimeOffset At(int minute, int second = 0) => start.AddMinutes(minute).AddSeconds(second);
        decimal P(DateTimeOffset t) => LastTradeBefore(events, BtcEur.Symbol, t);
        var stop1 = Math.Floor((P(At(2)) - 600m) / 10m) * 10m;
        var stop2 = Math.Floor((P(At(17)) - 150m) / 10m) * 10m;
        var steps = new List<OrderScriptStep>
        {
            Place(At(1), Order("g-entry-1", OrderSide.Buy, OrderType.Market, qty: 0.1m),
                expect: new() { Accepted = true, FinalStatus = OrderStatus.Filled }),
            Place(At(2), Order("g-stop-1", OrderSide.Sell, OrderType.Stop, qty: 0.1m, stop: stop1, tif: TimeInForce.Gtc),
                expect: new() { Accepted = true, FinalStatus = OrderStatus.Filled, ExecutedDuringGap = true, MaxFillPrice = stop1 },
                note: "Stop wird erst durch die nachgeladenen Kerzen der Standby-Lücke ausgelöst → Fill „während Lücke ausgeführt“."),
            Place(At(3), Order("g-lmt-cancel", OrderSide.Buy, OrderType.Limit, qty: 0.05m, limit: CryptoMarketSim.RoundTick(P(At(3)) - 300m), tif: TimeInForce.Gtc),
                expect: new() { Accepted = true }),
            Cancel(At(5), "g-lmt-cancel", new() { FinalStatus = OrderStatus.Cancelled },
                "Vor der Lücke storniert – darf in der Lücke nicht mehr gefüllt werden."),
            Place(At(16, 30), Order("g-entry-2", OrderSide.Buy, OrderType.Market, qty: 0.1m),
                CommandSource.Ai, AiClient, new() { Accepted = true, FinalStatus = OrderStatus.Filled }),
            Place(At(17), Order("g-stop-2", OrderSide.Sell, OrderType.Stop, qty: 0.1m, stop: stop2, tif: TimeInForce.Gtc),
                CommandSource.Ai, AiClient,
                new() { Accepted = true, FinalStatus = OrderStatus.Filled, ExecutedDuringGap = false, MaxFillPrice = stop2 - 200m },
                "Kurssprung 10:18:30 (−420 EUR): Fill zum ersten verfügbaren Kurs, nicht zum Stop-Preis."),
        };

        return new FixtureScenario(GapName, events, new OrderScript
        {
            Scenario = GapName,
            Description = "BTC/EUR mit Standby-Lücke 10:08–10:15 (GapDetected mit 7 nachgeladenen M1-Kerzen, Kurs −1.800 EUR) und Kurssprung −420 EUR um 10:18:30. Stop-Tests: Fill während der Lücke und Fill zum ersten Kurs nach dem Sprung.",
            Replay = GapName + ".jsonl",
            Seed = seed,
            Instruments = [BtcEur],
            Accounts = [MainAccount],
            Steps = steps,
        });
    }

    // ------------------------------------------------------------------ (c) verzögerte Aktie
    public static FixtureScenario CreateDelayed(int seed)
    {
        const string source = "gettex";
        const int delay = 15;
        var rng = new DeterministicRandom(seed);
        var start = new DateTimeOffset(2026, 11, 4, 8, 0, 0, TimeSpan.Zero); // 09:00 MEZ
        var end = start.AddHours(2);
        const decimal prevClose = 236.90m;
        decimal price = 237.40m, anchor = 237.40m;
        decimal dayHigh = 237.80m, dayLow = 236.55m, cumVolume = 18_450m;

        var trades = new List<(DateTimeOffset T, decimal Price, decimal Qty)>();
        var t = start.AddSeconds(rng.Next(1, 20)).AddMilliseconds(rng.Next(0, 1000));
        while (t < end)
        {
            anchor += 0.002m;
            price = Math.Round(price + rng.Normal() * 0.04m + (anchor - price) * 0.01m, 2);
            var r = rng.Next(0, 100);
            decimal qty = r < 70 ? rng.Next(1, 41) : r < 95 ? rng.Next(41, 201) : rng.Next(201, 1201);
            trades.Add((t, price, qty));
            t = t.AddSeconds(rng.Next(2, 46)).AddMilliseconds(rng.Next(0, 1000));
        }

        var asOfEvents = new List<MarketEvent>();
        decimal last = prevClose;
        int ti = 0;
        for (var minute = start; minute < end; minute = minute.AddMinutes(1))
        {
            var minuteEnd = minute.AddMinutes(1);
            decimal o = last, h = last, l = last, c = last, v = 0;
            int n = 0;
            for (; ti < trades.Count && trades[ti].T < minuteEnd; ti++)
            {
                var (tt, p, q) = trades[ti];
                if (n == 0) o = h = l = p;
                h = Math.Max(h, p);
                l = Math.Min(l, p);
                c = p;
                v += q;
                n++;
                cumVolume += q;
                dayHigh = Math.Max(dayHigh, p);
                dayLow = Math.Min(dayLow, p);
                asOfEvents.Add(new TradeTick
                {
                    Symbol = Sap.Symbol, Source = source, AsOfUtc = tt, DelayedMinutes = delay, Quality = DataQuality.Delayed,
                    Price = p, Quantity = q,
                });
            }
            last = c;
            asOfEvents.Add(new CandleUpdate
            {
                Symbol = Sap.Symbol, Source = source, AsOfUtc = minuteEnd, DelayedMinutes = delay, Quality = DataQuality.Delayed,
                Candle = new Candle
                {
                    Symbol = Sap.Symbol, Timeframe = Timeframe.M1, OpenTimeUtc = minute,
                    Open = o, High = h, Low = l, Close = c, Volume = v, TradeCount = n, Source = source, IsClosed = true,
                },
            });
            asOfEvents.Add(new QuoteTick
            {
                Symbol = Sap.Symbol, Source = source, AsOfUtc = minuteEnd, DelayedMinutes = delay, Quality = DataQuality.Delayed,
                Last = c, Volume24h = cumVolume, ChangePercent = Math.Round((c - prevClose) / prevClose * 100m, 2),
                High24h = dayHigh, Low24h = dayLow,
            });
        }

        // gettex veröffentlicht alle 15 Minuten eine Datei mit allem, was mindestens 15 Minuten alt ist.
        var quarter = TimeSpan.FromMinutes(15).Ticks;
        DateTimeOffset Publication(DateTimeOffset asOf) =>
            new DateTimeOffset((asOf.UtcTicks + quarter - 1) / quarter * quarter, TimeSpan.Zero).AddMinutes(15);
        var fetchLatency = new Dictionary<DateTimeOffset, TimeSpan>();
        for (var p = start.AddMinutes(15); p <= end.AddMinutes(15); p = p.AddMinutes(15))
            fetchLatency[p] = TimeSpan.FromSeconds(rng.Next(8, 26)) + TimeSpan.FromMilliseconds(rng.Next(0, 1000));

        var firstPoll = start.AddMinutes(15) + fetchLatency[start.AddMinutes(15)];
        var events = new List<MarketEvent>
        {
            new ConnectionStatusChanged
            {
                Symbol = "*", Source = source, AsOfUtc = firstPoll, ReceivedUtc = firstPoll, DelayedMinutes = delay,
                Quality = DataQuality.Delayed, State = ConnectionState.Connected, Message = "gettex-Datei abgerufen (Daten bis 15 Min. verzögert)",
            },
        };
        events.AddRange(asOfEvents
            .Select(e => e with { ReceivedUtc = Publication(e.AsOfUtc) + fetchLatency[Publication(e.AsOfUtc)] })
            .OrderBy(ReplayClock.TimeOf)
            .ThenBy(e => e.AsOfUtc));

        DateTimeOffset At(int hour, int minute) => new(2026, 11, 4, hour, minute, 0, TimeSpan.Zero);
        decimal Visible(DateTimeOffset at) => LastTradeBefore(events, Sap.Symbol, at);
        var limitBuy = Visible(At(8, 41)) - 1.00m;
        var limitSell = Math.Round(Visible(At(9, 5)) * 1.02m, 2);
        var steps = new List<OrderScriptStep>
        {
            Place(At(8, 40), Order("s-mkt-buy-1", OrderSide.Buy, OrderType.Market, qty: 20m, symbol: Sap.Symbol),
                expect: new() { Accepted = true, FillQuality = FillQuality.Synthetic, FillsOnlyFromMarketDataAfterUtc = At(8, 40) },
                note: "Sichtbare Kurse sind bei Ordereingang 25–40 Min. alt. Fill erst gegen einen Trade mit AsOfUtc nach 08:40 (kommt mit der Datei um ~09:00), angenommener Spread."),
            Place(At(8, 41), Order("s-lmt-buy-1", OrderSide.Buy, OrderType.Limit, qty: 30m, limit: limitBuy, symbol: Sap.Symbol),
                expect: new() { Accepted = true, MaxFillPrice = limitBuy, FillsOnlyFromMarketDataAfterUtc = At(8, 41) },
                note: "Limit 1 EUR unter dem zuletzt sichtbaren Kurs, gültig bis Handelsschluss (DAY)."),
            Place(At(9, 5), Order("s-lmt-sell-1", OrderSide.Sell, OrderType.Limit, qty: 10m, limit: limitSell, symbol: Sap.Symbol, tag: "delayed-test"),
                CommandSource.Ai, AiClient, new() { Accepted = true, MinFillPrice = limitSell }),
            Cancel(At(9, 50), "s-lmt-sell-1", new() { FinalStatus = OrderStatus.Cancelled }, source: CommandSource.Ai, client: AiClient),
            Place(At(9, 55), Order("s-mkt-sell-1", OrderSide.Sell, OrderType.Market, qty: 10m, symbol: Sap.Symbol),
                expect: new() { Accepted = true, FillsOnlyFromMarketDataAfterUtc = At(9, 55) },
                note: "Fill erst mit der letzten Datei (~10:15) gegen Trades nach 09:55."),
        };

        return new FixtureScenario(DelayedName, events, new OrderScript
        {
            Scenario = DelayedName,
            Description = "SAP SE (DE0007164600) über gettex, 15 Min. verzögert (Quality=Delayed), Handelstag-Ausschnitt 09:00–11:00 MEZ. Daten treffen in 15-Minuten-Dateien ein (ReceivedUtc = Veröffentlichung + Abruf). Prüft: kein Fill vor dem Ordereingang, synthetischer Spread, DAY-Orders.",
            Replay = DelayedName + ".jsonl",
            Seed = seed,
            Instruments = [Sap],
            Accounts = [MainAccount],
            Steps = steps,
        });
    }

    // ------------------------------------------------------------------ Hilfen
    public static decimal LastTradeBefore(IReadOnlyList<MarketEvent> events, string symbol, DateTimeOffset replayTime) =>
        events.OfType<TradeTick>().Where(e => e.Symbol == symbol && ReplayClock.TimeOf(e) <= replayTime).Last().Price;

    private static OrderScriptPlaceOrder Order(string clientOrderId, OrderSide side, OrderType type,
        decimal? qty = null, decimal? notional = null, decimal? limit = null, decimal? stop = null,
        TimeInForce tif = TimeInForce.Day, string? tag = null, string? symbol = null) => new()
    {
        ClientOrderId = clientOrderId, Symbol = symbol ?? BtcEur.Symbol, Side = side, Type = type,
        Quantity = qty, Notional = notional, LimitPrice = limit, StopPrice = stop, TimeInForce = tif, StrategyTag = tag,
    };

    private static OrderScriptStep Place(DateTimeOffset at, OrderScriptPlaceOrder order, CommandSource source = CommandSource.Human,
        string? client = null, OrderScriptExpectation? expect = null, string? note = null) => new()
    {
        AtUtc = at, Action = OrderScriptAction.PlaceOrder, AccountId = "main", Source = source, ClientName = client,
        PlaceOrder = order, Expect = expect, Note = note,
    };

    private static OrderScriptStep Cancel(DateTimeOffset at, string clientOrderId, OrderScriptExpectation? expect = null,
        string? note = null, CommandSource source = CommandSource.Human, string? client = null) => new()
    {
        AtUtc = at, Action = OrderScriptAction.Cancel, AccountId = "main", Source = source, ClientName = client,
        Cancel = new OrderScriptCancel { ClientOrderId = clientOrderId }, Expect = expect, Note = note,
    };
}
