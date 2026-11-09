using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Charting.Tests;

public class CsvExportTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 9, 13, 45, 30, TimeSpan.Zero);

    private static Fill SampleFill(string? tag = "scalp") => new()
    {
        FillId = "fil_1",
        OrderId = "ord_1",
        AccountId = "main",
        Symbol = "BTC/EUR",
        Side = OrderSide.Sell,
        Quantity = 0.125m,
        Price = 61234.5m,
        Fee = 30.62m,
        FxRate = 1m,
        RealizedPnl = -12.5m,
        Quality = FillQuality.Gap,
        ExecutedDuringGap = true,
        Source = CommandSource.Ai,
        StrategyTag = tag,
        TimestampUtc = T,
        MarketDataAsOfUtc = T,
    };

    [Fact]
    public void Invariant_uses_comma_dot_and_iso_utc()
    {
        var csv = TradeCsv.Fills([SampleFill()], CsvFormat.Invariant);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Zeit,Symbol,Seite,Menge,Preis,", lines[0]);
        Assert.Equal("2026-11-09T13:45:30Z,BTC/EUR,Verkauf,0.125,61234.5,30.62,1,-12.5,Lücke,Taker,ja,KI,scalp,2026-11-09T13:45:30Z,ord_1,fil_1", lines[1]);
    }

    [Fact]
    public void German_excel_uses_semicolon_decimal_comma_and_local_time()
    {
        var berlin = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
        var csv = TradeCsv.Fills([SampleFill()], CsvFormat.GermanExcel(berlin));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Zeit;Symbol;Seite;Menge;Preis;", lines[0]);
        Assert.Equal("09.11.2026 14:45:30;BTC/EUR;Verkauf;0,125;61234,5;30,62;1;-12,5;Lücke;Taker;ja;KI;scalp;09.11.2026 14:45:30;ord_1;fil_1", lines[1]);
        Assert.True(CsvFormat.GermanExcel().Utf8Bom);
        Assert.Equal(3, CsvFormat.GermanExcel().Encoding.GetPreamble().Length);
        Assert.Empty(CsvFormat.Invariant.Encoding.GetPreamble());
    }

    [Fact]
    public void Fields_with_separator_or_quotes_are_escaped()
    {
        var csv = TradeCsv.Fills([SampleFill("a;b \"c\"")], CsvFormat.GermanExcel(TimeZoneInfo.Utc));
        Assert.Contains(";\"a;b \"\"c\"\"\";", csv);
        var inv = TradeCsv.Fills([SampleFill("x,y")], CsvFormat.Invariant);
        Assert.Contains(",\"x,y\",", inv);
    }

    [Fact]
    public void Formula_injection_in_text_is_neutralised_but_negative_numbers_stay_numbers()
    {
        var csv = TradeCsv.Fills([SampleFill("=HYPERLINK(\"x\")")], CsvFormat.Invariant);
        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\",", csv);
        Assert.Contains(",-12.5,", csv);
    }

    [Fact]
    public void Orders_export_contains_status_and_empty_optional_fields()
    {
        var order = new Order
        {
            OrderId = "ord_9",
            ClientOrderId = "cli_9",
            AccountId = "main",
            Symbol = "DE0007164600",
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            TimeInForce = TimeInForce.Gtc,
            Quantity = 10m,
            LimitPrice = 120.5m,
            Source = CommandSource.Human,
            ClientName = "ui",
            CreatedUtc = T,
            Status = OrderStatus.Working,
        };
        var csv = TradeCsv.Orders([order], CsvFormat.Invariant);
        var line = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Equal("2026-11-09T13:45:30Z,DE0007164600,Kauf,Limit,10,0,120.5,,,0,Working,GTC,Mensch,ui,,ord_9,cli_9", line);
    }

    [Fact]
    public void Audit_export_has_header_and_duration()
    {
        var e = new AuditEntry
        {
            AuditId = "a1",
            TimestampUtc = T,
            Source = CommandSource.Ai,
            ClientId = "claude",
            Action = "place_order",
            ParametersJson = "{\"symbol\":\"BTC/EUR\"}",
            Result = "Executed",
            Duration = TimeSpan.FromMilliseconds(42),
        };
        var csv = TradeCsv.Audit([e], CsvFormat.Invariant);
        Assert.Contains("\"{\"\"symbol\"\":\"\"BTC/EUR\"\"}\"", csv);
        Assert.EndsWith(",42\r\n", csv);
    }
}
