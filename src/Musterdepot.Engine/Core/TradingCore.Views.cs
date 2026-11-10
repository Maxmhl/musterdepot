using Musterdepot.Core.Domain;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Market;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

public static partial class TradingCore
{
    /// <summary>Depotansicht zu aktuellen Kursen (Live-Markt, sonst gespeicherter Bewertungskurs).</summary>
    public static PortfolioSnapshot BuildPortfolio(AccountState s, MarketState market, EngineContext ctx)
    {
        var positions = new List<PositionSnapshot>();
        foreach (var (symbol, lots) in s.Lots)
        {
            if (lots.Count == 0) continue;
            var qty = lots.Sum(l => l.Quantity);
            var costBasis = lots.Sum(l => l.CostInclFees);
            var avg = qty == 0 ? 0 : Money.Normalize(Math.Round(lots.Sum(l => l.Quantity * l.Price) / qty, 10, MidpointRounding.AwayFromZero));
            var val = ValuationPrice(s, symbol, market, ctx);
            decimal? mv = val is { } v ? Money.Round(qty * v.Price * v.Fx) : null;
            var sym = market.Get(symbol);
            var inst = ctx.Instruments.Resolve(symbol);
            positions.Add(new PositionSnapshot
            {
                Symbol = symbol,
                Quantity = qty,
                AverageCost = avg,
                CostBasis = costBasis,
                LastPrice = val?.Price,
                MarketValue = mv,
                UnrealizedPnl = mv - costBasis,
                UnrealizedPnlPercent = mv is { } m && costBasis != 0 ? Math.Round((m - costBasis) / costBasis * 100m, 2, MidpointRounding.AwayFromZero) : null,
                RealizedPnl = s.RealizedBySymbol.TryGetValue(symbol, out var r) ? r : 0m,
                QuoteCurrency = inst?.QuoteCurrency ?? s.BaseCurrency,
                PriceIsDelayed = sym?.IsDelayed ?? false,
                PriceAsOfUtc = sym?.LastAsOfUtc ?? (s.Marks.TryGetValue(symbol, out var mk) ? mk.AsOfUtc : null),
                Lots = lots.Select(l => l.ToLot()).ToList(),
            });
        }
        var positionsValue = positions.Sum(p => p.MarketValue ?? p.CostBasis);
        var equity = s.Cash + positionsValue;
        var dayStart = s.DayStartEquityFor(BerlinTime.TradingDay(ctx.Now));
        return new PortfolioSnapshot
        {
            AccountId = s.AccountId,
            BaseCurrency = s.BaseCurrency,
            AsOfUtc = ctx.Now,
            Cash = s.Cash,
            ReservedCash = ReservedCash(s, market, ctx),
            PositionsValue = positionsValue,
            RealizedPnl = s.RealizedPnl,
            UnrealizedPnl = positions.Sum(p => p.UnrealizedPnl ?? 0m),
            FeesPaid = s.FeesPaid,
            DayStartEquity = dayStart,
            DayPnl = equity - dayStart,
            KillSwitchEngaged = s.KillSwitchEngaged,
            DailyLossLimitHit = s.IsDailyLossLimitHit(BerlinTime.TradingDay(ctx.Now)),
            Positions = positions,
        };
    }
}
