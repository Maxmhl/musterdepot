using System.Text.RegularExpressions;
using Musterdepot.Core.Domain;

namespace Musterdepot.MarketData.Infrastructure;

/// <summary>
/// Programmweites Symbolformat: Krypto "BASE/QUOTE" (z. B. "BTC/EUR"), Aktien = ISIN ("DE0007164600").
/// </summary>
public static partial class SymbolHelper
{
    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{9}[0-9]$")]
    private static partial Regex IsinPattern();

    [GeneratedRegex("^[A-Z0-9]{1,15}/[A-Z0-9]{2,6}$")]
    private static partial Regex PairPattern();

    /// <summary>Prüft Aufbau und Prüfziffer einer ISIN.</summary>
    public static bool IsIsin(string? s)
    {
        if (s is null || !IsinPattern().IsMatch(s)) return false;
        // Buchstaben → Zahlen (A=10 … Z=35), dann Luhn über die Ziffernfolge.
        var digits = new System.Text.StringBuilder(24);
        foreach (var c in s[..11])
            digits.Append(char.IsAsciiDigit(c) ? (c - '0').ToString() : (c - 'A' + 10).ToString());
        int sum = 0;
        bool dbl = true;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }
        int check = (10 - sum % 10) % 10;
        return check == s[11] - '0';
    }

    /// <summary>Sieht nach ISIN aus (ohne Prüfziffer-Test) – für tolerantes Einlesen.</summary>
    public static bool LooksLikeIsin(string? s) => s is not null && IsinPattern().IsMatch(s);

    public static bool IsCryptoPair(string? s) => s is not null && PairPattern().IsMatch(s);

    public static bool TrySplitPair(string symbol, out string baseAsset, out string quoteAsset)
    {
        var i = symbol.IndexOf('/');
        if (i <= 0 || i == symbol.Length - 1) { baseAsset = quoteAsset = ""; return false; }
        baseAsset = symbol[..i];
        quoteAsset = symbol[(i + 1)..];
        return true;
    }

    public static AssetClass? AssetClassOf(string symbol)
        => IsCryptoPair(symbol) ? AssetClass.Crypto : LooksLikeIsin(symbol) ? AssetClass.Stock : null;

    public static string QuoteCurrencyOf(string symbol)
        => TrySplitPair(symbol, out _, out var q) ? q : "EUR";

    /// <summary>"BTC-EUR" (Bitvavo/Coinbase) → "BTC/EUR".</summary>
    public static string FromDashed(string market) => market.Replace('-', '/').ToUpperInvariant();

    /// <summary>"BTC/EUR" → "BTC-EUR".</summary>
    public static string ToDashed(string symbol) => symbol.Replace('/', '-');

    /// <summary>Kraken-Asset-Codes normalisieren (XBT → BTC, XDG → DOGE, XXBT → BTC, ZEUR → EUR).</summary>
    public static string NormalizeKrakenAsset(string asset)
    {
        var a = asset.ToUpperInvariant();
        if (a.Length == 4 && (a[0] == 'X' || a[0] == 'Z') && a is not ("XTZ" or "ZRX") && !a.StartsWith("XC", StringComparison.Ordinal))
            a = a[1..];
        return a switch { "XBT" => "BTC", "XDG" => "DOGE", _ => a };
    }

    /// <summary>Programmsymbol → Kraken-REST-Paarname (altname), wenn AssetPairs noch nicht geladen sind.</summary>
    public static string ToKrakenRestPair(string symbol)
    {
        if (!TrySplitPair(symbol, out var b, out var q)) return symbol;
        b = b switch { "BTC" => "XBT", "DOGE" => "XDG", _ => b };
        return b + q;
    }

    public static Instrument SynthesizeCryptoInstrument(string symbol, string? source = null)
    {
        TrySplitPair(symbol, out var b, out var q);
        return new Instrument
        {
            Symbol = symbol,
            Name = $"{b} / {q}",
            AssetClass = AssetClass.Crypto,
            QuoteCurrency = q,
            BaseAsset = b,
            TickSize = 0.01m,
            QuantityStep = 0.00000001m,
            PrimarySource = source,
            SourceSymbol = symbol,
        };
    }
}
