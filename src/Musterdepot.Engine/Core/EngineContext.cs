using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Instruments;
using Musterdepot.Engine.Market;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

/// <summary>Alles, was <c>Decide</c> von außen braucht: Uhrzeit, ID-Erzeugung, Stammdaten, Kalender, Umrechnungskurse, Annahmen.</summary>
public sealed record EngineContext
{
    public required DateTimeOffset Now { get; init; }
    public required IIdGenerator Ids { get; init; }
    public IInstrumentResolver Instruments { get; init; } = HeuristicInstrumentResolver.Instance;
    public ITradingCalendar Calendar { get; init; } = new GettexTradingCalendar();
    public IFxRateProvider? Fx { get; init; }
    public ExecutionConfig Execution { get; init; } = new();
    public EngineOptions Options { get; init; } = EngineOptions.Default;

    /// <summary>
    /// Umrechnungskurs 1 <paramref name="from"/> → <paramref name="to"/>: gleich = 1; sonst <see cref="IFxRateProvider"/>;
    /// sonst Näherung aus Marktdaten (z. B. USDC/EUR).
    /// </summary>
    public (decimal Rate, string? Source)? ResolveFx(string from, string to, MarketState market)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return (1m, null);
        if (Fx?.GetRate(from, to) is { } r && r > 0)
            return (r, string.IsNullOrWhiteSpace(Fx.SourceDescription) ? "fx-provider" : Fx.SourceDescription);
        if (market.FxFromMarket(from.ToUpperInvariant(), to.ToUpperInvariant()) is { } m) return (m.Rate, m.Source);
        return null;
    }
}

/// <summary>Ergebnis von <c>Decide</c> für einen Befehl.</summary>
public sealed record CommandDecision
{
    public required bool Accepted { get; init; }
    public IReadOnlyList<DomainEvent> Events { get; init; } = [];
    public string? RejectReason { get; init; }
    public string? RiskRule { get; init; }
    public string? OrderId { get; init; }
}

/// <summary>Namen der Risikoregeln (stehen in <see cref="OrderRejected.RiskRule"/> bzw. <see cref="EngineResult.RiskRule"/>).</summary>
public static class RiskRules
{
    public const string KillSwitch = "KillSwitch";
    public const string AllowedSymbols = "AllowedSymbols";
    public const string MaxOrdersPerMinute = "MaxOrdersPerMinute";
    public const string MaxOrderNotional = "MaxOrderNotional";
    public const string MaxOrderPercentOfEquity = "MaxOrderPercentOfEquity";
    public const string MaxPositionNotional = "MaxPositionNotional";
    public const string MaxPositionPercentOfEquity = "MaxPositionPercentOfEquity";
    public const string MaxDailyLoss = "MaxDailyLoss";
    public const string PriceBand = "PriceBand";
    public const string MaxOpenOrders = "MaxOpenOrders";
    public const string MaxQuoteAge = "MaxQuoteAge";
    public const string MarketHours = "MarketHours";
    public const string NoMarketData = "NoMarketData";
    public const string NoShortSelling = "NoShortSelling";
    public const string InsufficientCash = "InsufficientCash";
    public const string FxUnavailable = "FxUnavailable";
    public const string HumanOnly = "HumanOnly";
}

/// <summary>Arbeitskontext eines Decide-Aufrufs: sammelt Events und wendet sie sofort per Evolve an.</summary>
internal sealed class DecisionSession(AccountState state, MarketState market, EngineContext ctx)
{
    public AccountState State { get; private set; } = state;
    public MarketState Market { get; } = market;
    public EngineContext Ctx { get; } = ctx;
    public List<DomainEvent> Events { get; } = [];
    public DateTimeOffset Now => Ctx.Now;
    public string AccountId => State.AccountId;

    /// <summary>Im selben Decide-Aufruf bereits verbrauchte Orderbuch-Mengen (Symbol, Seite, Preis).</summary>
    public Dictionary<(string Symbol, OrderSide BookSide, decimal Price), decimal> BookConsumed { get; } = [];
    /// <summary>Im selben Decide-Aufruf bereits genutztes Trade-Volumen für Maker-Fills.</summary>
    public decimal MakerVolumeUsed { get; set; }

    public T Emit<T>(T e) where T : DomainEvent
    {
        Events.Add(e);
        State = TradingCore.Evolve(State, e);
        return e;
    }

    public Order Reload(Order o) => State.Orders[o.OrderId];
}
