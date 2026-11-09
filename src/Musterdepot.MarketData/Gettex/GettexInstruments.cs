using Musterdepot.Core.Domain;

namespace Musterdepot.MarketData.Gettex;

/// <summary>Eingebaute Liste bekannter DAX-Werte für die Symbolsuche (Symbol = ISIN).</summary>
public static class GettexInstruments
{
    private static Instrument Stock(string isin, string wkn, string name) => new()
    {
        Symbol = isin,
        Name = name,
        AssetClass = AssetClass.Stock,
        QuoteCurrency = "EUR",
        BaseAsset = isin,
        Isin = isin,
        Wkn = wkn,
        TickSize = 0.001m,
        QuantityStep = 1m,
        MinQuantity = 1m,
        PrimarySource = "gettex",
        SourceSymbol = isin,
    };

    public static IReadOnlyList<Instrument> Dax { get; } =
    [
        Stock("DE000A1EWWW0", "A1EWWW", "adidas AG"),
        Stock("NL0000235190", "938914", "Airbus SE"),
        Stock("DE0008404005", "840400", "Allianz SE"),
        Stock("DE000BASF111", "BASF11", "BASF SE"),
        Stock("DE000BAY0017", "BAY001", "Bayer AG"),
        Stock("DE0005200000", "520000", "Beiersdorf AG"),
        Stock("DE0005190003", "519000", "BMW AG"),
        Stock("DE000A1DAHH0", "A1DAHH", "Brenntag SE"),
        Stock("DE000CBK1001", "CBK100", "Commerzbank AG"),
        Stock("DE0005439004", "543900", "Continental AG"),
        Stock("DE0006062144", "606214", "Covestro AG"),
        Stock("DE000DTR0CK8", "DTR0CK", "Daimler Truck Holding AG"),
        Stock("DE0005140008", "514000", "Deutsche Bank AG"),
        Stock("DE0005810055", "581005", "Deutsche Börse AG"),
        Stock("DE0005552004", "555200", "DHL Group (Deutsche Post AG)"),
        Stock("DE0005557508", "555750", "Deutsche Telekom AG"),
        Stock("DE000ENAG999", "ENAG99", "E.ON SE"),
        Stock("DE0005785604", "578560", "Fresenius SE & Co. KGaA"),
        Stock("DE0008402215", "840221", "Hannover Rück SE"),
        Stock("DE0006047004", "604700", "Heidelberg Materials AG"),
        Stock("DE0006048432", "604843", "Henkel AG & Co. KGaA Vz."),
        Stock("DE0006231004", "623100", "Infineon Technologies AG"),
        Stock("DE0007100000", "710000", "Mercedes-Benz Group AG"),
        Stock("DE0006599905", "659990", "Merck KGaA"),
        Stock("DE000A0D9PT0", "A0D9PT", "MTU Aero Engines AG"),
        Stock("DE0008430026", "843002", "Münchener Rück AG"),
        Stock("DE000PAG9113", "PAG911", "Dr. Ing. h.c. F. Porsche AG Vz."),
        Stock("DE000PAH0038", "PAH003", "Porsche Automobil Holding SE Vz."),
        Stock("DE0007030009", "703000", "Rheinmetall AG"),
        Stock("DE0007037129", "703712", "RWE AG"),
        Stock("DE0007164600", "716460", "SAP SE"),
        Stock("DE0007165631", "716563", "Sartorius AG Vz."),
        Stock("DE0007236101", "723610", "Siemens AG"),
        Stock("DE000ENER6Y0", "ENER6Y", "Siemens Energy AG"),
        Stock("DE000SHL1006", "SHL100", "Siemens Healthineers AG"),
        Stock("DE000SYM9999", "SYM999", "Symrise AG"),
        Stock("DE0007664039", "766403", "Volkswagen AG Vz."),
        Stock("DE000A1ML7J1", "A1ML7J", "Vonovia SE"),
        Stock("DE000ZAL1111", "ZAL111", "Zalando SE"),
    ];

    private static readonly Dictionary<string, Instrument> ByIsin = Dax.ToDictionary(i => i.Symbol, StringComparer.Ordinal);

    public static Instrument? Find(string isin) => ByIsin.GetValueOrDefault(isin);

    /// <summary>Instrument zu einer ISIN – aus der Liste oder minimal erzeugt.</summary>
    public static Instrument ForIsin(string isin, string source = "gettex") => Find(isin) ?? Stock(isin, "", isin) with { PrimarySource = source, Wkn = null };
}
