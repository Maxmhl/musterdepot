using System.Text.Json;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.Serialization;
using Musterdepot.Engine;

namespace Musterdepot.IntegrationTests.Infrastructure;

/// <summary>Invarianten, die nur über die öffentliche Schnittstelle <see cref="ITradingEngine"/> geprüft werden.</summary>
public static class Invariants
{
    /// <summary>Geld bleibt erhalten, keine negativen Mengen, Limit-Fills nie schlechter als das Limit.</summary>
    public static void CheckAccount(ITradingEngine engine, string accountId)
    {
        var account = engine.GetAccounts().Single(a => a.AccountId == accountId);
        var p = engine.GetPortfolio(accountId);
        var fills = engine.GetFills(accountId);
        var cash = engine.GetCashEntries(accountId);
        var orders = engine.GetOrders(accountId).ToDictionary(o => o.OrderId);

        // Geld bleibt erhalten: Cash = Summe aller Buchungen = Start + Einzahlungen − Käufe + Verkäufe − Gebühren.
        Assert.Equal(cash.Sum(c => c.Amount), p.Cash);
        var deposits = cash.Where(c => c.Kind is "deposit" or "withdrawal").Sum(c => c.Amount);
        var flows = fills.Sum(f => (f.Side == OrderSide.Buy ? -1 : 1) * Money.Round(f.Quantity * f.Price * f.FxRate) - f.Fee);
        Assert.Equal(account.InitialCash + deposits + flows, p.Cash);
        Assert.Equal(fills.Sum(f => f.Fee), p.FeesPaid);
        Assert.Equal(p.Cash + p.PositionsValue, p.Equity);
        Assert.True(p.Cash >= 0, $"Cash negativ: {p.Cash}");

        // Keine negativen Mengen.
        Assert.All(p.Positions, pos => Assert.True(pos.Quantity >= 0, $"{pos.Symbol}: Menge {pos.Quantity}"));
        foreach (var o in orders.Values)
            Assert.True(o.FilledQuantity >= 0 && o.FilledQuantity <= o.Quantity, $"{o.OrderId}: {o.FilledQuantity}/{o.Quantity}");
        var netBySymbol = fills.GroupBy(f => f.Symbol).ToDictionary(g => g.Key, g => g.Sum(f => f.Side == OrderSide.Buy ? f.Quantity : -f.Quantity));
        foreach (var (symbol, qty) in netBySymbol)
        {
            Assert.True(qty >= 0, $"Leerverkauf {symbol}: {qty}");
            Assert.Equal(qty, p.Positions.FirstOrDefault(x => x.Symbol == symbol)?.Quantity ?? 0m);
        }

        // Limit-Fills nie schlechter als das Limit.
        foreach (var f in fills)
        {
            Assert.True(f.Quantity > 0 && f.Price > 0 && f.Fee >= 0, $"Fill {f.FillId} ungültig");
            var o = orders[f.OrderId];
            if (o.Type is OrderType.Limit or OrderType.StopLimit && o.LimitPrice is { } lim)
                Assert.True(f.Side == OrderSide.Buy ? f.Price <= lim : f.Price >= lim, $"Limit-Fill {f.Price} schlechter als {lim} ({o.ClientOrderId})");
        }
    }

    /// <summary>Risiko-Gate nie umgangen – nachträglich aus dem Event-Log geprüft.</summary>
    public static void CheckRiskGate(IReadOnlyList<DomainEvent> events, Func<DateTimeOffset, decimal?> lastPriceAt)
    {
        var kill = false;
        RiskConfig risk = RiskConfig.Default;
        var position = new Dictionary<string, decimal>();
        foreach (var e in events)
        {
            switch (e)
            {
                case AccountOpened ao: risk = ao.Risk; break;
                case RiskConfigChanged rc: risk = rc.Risk; break;
                case KillSwitchEngaged: kill = true; break;
                case KillSwitchReleased: kill = false; break;
                case OrderSubmitted { Order: var o } when o.ParentOrderId is null:
                    Assert.False(kill, $"Order {o.ClientOrderId} trotz Not-Aus angenommen");
                    if (o.Side == OrderSide.Buy && o.Type == OrderType.Limit && o.LimitPrice is { } lp && risk.MaxOrderNotional is { } max)
                        Assert.True(o.Quantity * lp <= max, $"MaxOrderNotional überschritten: {o.ClientOrderId} {o.Quantity}×{lp}");
                    if (o.Type is OrderType.Limit && o.LimitPrice is { } lim && lastPriceAt(e.TimestampUtc) is { } last)
                    {
                        var deviation = Math.Abs(lim - last) / last * 100m;
                        Assert.True(deviation <= risk.PriceBandPercent + 0.5m, $"Preisband verletzt: {o.ClientOrderId} Limit {lim}, letzter Kurs {last}");
                    }
                    break;
                case OrderFilled f:
                    var sign = f.Fill.Side == OrderSide.Buy ? 1 : -1;
                    position[f.Fill.Symbol] = position.GetValueOrDefault(f.Fill.Symbol) + sign * f.Fill.Quantity;
                    Assert.True(position[f.Fill.Symbol] >= 0, "Leerverkauf im Event-Log");
                    break;
            }
        }
    }

    /// <summary>Zustand, der allein aus Events folgt (ohne Marktbewertung und ohne die vom Kurs abhängige Reservierung
    /// für Market-/Stop-Orders) – für den Vergleich Live ↔ wiederhergestellt.</summary>
    public static string StateFingerprint(ITradingEngine engine)
    {
        var accounts = engine.GetAccounts().OrderBy(a => a.AccountId).Select(a =>
        {
            var p = engine.GetPortfolio(a.AccountId);
            return new
            {
                Account = a,
                p.Cash,
                p.RealizedPnl,
                p.FeesPaid,
                p.KillSwitchEngaged,
                Positions = p.Positions.Select(x => new { x.Symbol, x.Quantity, x.CostBasis, x.AverageCost, x.RealizedPnl, x.Lots }),
                Orders = engine.GetOrders(a.AccountId).OrderBy(o => o.OrderId),
                Fills = engine.GetFills(a.AccountId),
                CashEntries = engine.GetCashEntries(a.AccountId),
                Risk = engine.GetRiskConfig(a.AccountId),
                Events = engine.GetEvents(a.AccountId).Count,
            };
        });
        return JsonSerializer.Serialize(accounts, GoldenFile.JsonOptions);
    }
}
