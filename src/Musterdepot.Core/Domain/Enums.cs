namespace Musterdepot.Core.Domain;

public enum AssetClass { Crypto, Stock, Etf, Fx }

public enum OrderSide { Buy, Sell }

/// <summary>v1: Market, Limit, Stop, StopLimit. v1.1: TrailingStop (OCO und Bracket über Gruppen-Felder am Order).</summary>
public enum OrderType { Market, Limit, Stop, StopLimit, TrailingStop }

/// <summary>v1: Day, Gtc, Ioc. Später: Fok, Gtd.</summary>
public enum TimeInForce { Day, Gtc, Ioc, Fok, Gtd }

public enum OrderStatus { PendingNew, Working, Triggered, PartiallyFilled, Filled, Cancelled, Rejected, Expired }

/// <summary>Wer hat einen Befehl/eine Order ausgelöst.</summary>
public enum CommandSource { Human, Ai }

/// <summary>Qualität der Marktdaten (Schnittstelle A).</summary>
public enum DataQuality { Realtime, Delayed, Replay, Synthetic, Backfill }

/// <summary>Wie ein Fill zustande kam.</summary>
public enum FillQuality { OrderBook, Trade, Synthetic, Gap }

public enum Liquidity { Maker, Taker }

public enum BracketRole { None, Entry, TakeProfit, StopLoss }

public enum Timeframe { M1, M5, M15, M30, H1, H4, D1, W1 }

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Suspended, Failed }

/// <summary>Modus eines KI-Clients, nur in der Oberfläche einstellbar.</summary>
public enum ClientMode { ReadOnly, Confirm, Autonomous }

[Flags]
public enum ClientScopes { None = 0, Read = 1, Trade = 2, Settings = 4, All = Read | Trade | Settings }

public enum FeeModelKind { None, Fixed, Percent, Tiered, Combined }

public static class TimeframeExtensions
{
    public static TimeSpan ToTimeSpan(this Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        Timeframe.W1 => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    /// <summary>Beginn der Kerze, in die <paramref name="t"/> fällt (UTC, W1 beginnt Montag).</summary>
    public static DateTimeOffset Floor(this Timeframe tf, DateTimeOffset t)
    {
        t = t.ToUniversalTime();
        if (tf == Timeframe.W1)
        {
            var day = new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, TimeSpan.Zero);
            int diff = ((int)day.DayOfWeek + 6) % 7;
            return day.AddDays(-diff);
        }
        var ticks = tf.ToTimeSpan().Ticks;
        return new DateTimeOffset(t.UtcTicks - (t.UtcTicks % ticks), TimeSpan.Zero);
    }
}
