using System.Globalization;
using System.IO.Compression;
using System.Text;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Gettex;

/// <summary>Ein Geschäft aus einer gettex-Datei.</summary>
public sealed record GettexTrade
{
    public required string Isin { get; init; }
    public required DateTimeOffset TimeUtc { get; init; }
    public required decimal Price { get; init; }
    public decimal Volume { get; init; }
    public string Currency { get; init; } = "EUR";
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? BidSize { get; init; }
    public decimal? AskSize { get; init; }
}

public sealed record GettexParseResult
{
    public required IReadOnlyList<GettexTrade> Trades { get; init; }
    public char Delimiter { get; init; }
    public bool HasHeader { get; init; }
    /// <summary>Kennung des Formats (Trennzeichen + Spalten) zur Erkennung von Formatwechseln.</summary>
    public required string Fingerprint { get; init; }
    /// <summary>ISIN- und Preisspalte gefunden.</summary>
    public bool FormatRecognized { get; init; }
    /// <summary>Format weicht von der zuvor gelesenen Datei ab.</summary>
    public bool FormatChanged { get; init; }
    public string? PreviousFingerprint { get; init; }
    public bool DecimalComma { get; init; }
    public int SkippedLines { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Erkannte Spaltenrollen (zur Diagnose).</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];
}

/// <summary>
/// Robuster Parser für gettex-CSV-Dateien: erkennt Trennzeichen (; , Tab |), Kopfzeilen auf Deutsch/Englisch,
/// Dezimalkomma, gzip, Zeiten in Europe/Berlin (→ UTC) und meldet Formatwechsel über einen Header-Fingerabdruck.
/// Wirft bei kaputten Daten keine Ausnahme, sondern sammelt Warnungen.
/// </summary>
public sealed class GettexCsvParser(GettexParserOptions? options = null)
{
    internal enum Role { Ignore, Isin, DateTime, Date, Time, Price, Volume, Bid, Ask, BidSize, AskSize, Currency }

    private static readonly Dictionary<string, Role> Aliases = BuildAliases();
    private readonly GettexParserOptions _options = options ?? new GettexParserOptions();

    /// <summary>Fingerabdruck der zuletzt erkannten Datei.</summary>
    public string? LastFingerprint { get; private set; }

    private static Dictionary<string, Role> BuildAliases()
    {
        var d = new Dictionary<string, Role>(StringComparer.Ordinal);
        void Add(Role r, params string[] names) { foreach (var n in names) d[n] = r; }
        Add(Role.Isin, "isin", "isincode", "instrument", "instrumentid", "instrumentidentificationcode", "wertpapier", "wertpapierisin", "security", "securityid");
        Add(Role.DateTime, "zeitstempel", "timestamp", "datetime", "datumzeit", "datumuhrzeit", "tradingdatetime", "handelszeitpunkt", "tradetimestamp", "executiontime", "ausfuehrungszeit");
        Add(Role.Date, "datum", "date", "tradedate", "handelstag", "handelsdatum", "tradingday", "tradingdate", "day", "tag");
        Add(Role.Time, "zeit", "time", "uhrzeit", "handelszeit", "tradetime", "tradingtime", "zeitpunkt");
        Add(Role.Price, "preis", "price", "kurs", "last", "lastprice", "letzterpreis", "letzterkurs", "tradeprice", "ausfuehrungspreis", "ausfuehrungskurs", "executionprice", "px");
        Add(Role.Volume, "volumen", "volume", "menge", "quantity", "qty", "stueck", "stueckzahl", "anzahl", "size", "tradesize", "nominal", "umsatzstueck", "volumenstueck", "shares");
        Add(Role.Bid, "bid", "geld", "geldkurs", "bidprice", "bestbid");
        Add(Role.Ask, "ask", "brief", "briefkurs", "askprice", "offer", "bestask");
        Add(Role.BidSize, "bidsize", "bidvolume", "bidqty", "geldvolumen", "geldmenge");
        Add(Role.AskSize, "asksize", "askvolume", "askqty", "briefvolumen", "briefmenge");
        Add(Role.Currency, "waehrung", "currency", "ccy", "whrg", "pricecurrency", "priceccy", "notationcurrency", "handelswaehrung");
        return d;
    }

    internal static string NormalizeHeader(string h)
    {
        var s = h.Trim().Trim('"', '\'', '﻿').ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Liest eine Datei/einen Stream (gzip wird an den Magic Bytes erkannt).</summary>
    public GettexParseResult Parse(Stream stream, DateOnly? fileDate = null)
    {
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            bytes = outMs.ToArray();
        }
        return Parse(DecodeText(bytes), fileDate);
    }

    private static string DecodeText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public GettexParseResult Parse(string content, DateOnly? fileDate = null)
    {
        var warnings = new List<string>();
        void Warn(string w) { if (warnings.Count < _options.MaxWarnings) warnings.Add(w); }

        var lines = content.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToList();
        if (lines.Count == 0)
            return Result([], ';', false, "empty", true, warnings, 0, false, []);

        var delimiter = DetectDelimiter(lines.Take(10).ToList());
        var first = SplitLine(lines[0], delimiter);

        // Kopfzeile?
        Role[] roles;
        bool hasHeader = false;
        string columnsKey;
        var headerRoles = first.Select(f => Aliases.TryGetValue(NormalizeHeader(f), out var r) ? r : Role.Ignore).ToArray();
        bool firstLooksLikeData = first.Any(f => SymbolHelper.LooksLikeIsin(f.Trim().Trim('"')));
        if (!firstLooksLikeData && headerRoles.Contains(Role.Price) && headerRoles.Contains(Role.Isin))
        {
            hasHeader = true;
            roles = DeduplicateRoles(headerRoles);
            columnsKey = string.Join(",", first.Select(NormalizeHeader));
        }
        else if (!firstLooksLikeData && first.All(f => !TryParseNumberLoose(f, out _)) && first.Any(f => Aliases.ContainsKey(NormalizeHeader(f))))
        {
            // Kopfzeile vorhanden, aber ohne ISIN- oder Preisspalte → Format nicht erkannt.
            var fp = $"H|{DelimiterName(delimiter)}|{string.Join(",", first.Select(NormalizeHeader))}";
            Warn($"Kopfzeile ohne ISIN- oder Preisspalte: {string.Join(delimiter, first)}");
            return Finish([], delimiter, true, fp, false, warnings, lines.Count - 1, false, first.Select(NormalizeHeader).ToList());
        }
        else
        {
            if (!firstLooksLikeData)
            {
                // Unbekannte Kopfzeile ohne Daten → überspringen und Spalten heuristisch bestimmen.
                hasHeader = !first.Any(f => TryParseNumberLoose(f, out _));
            }
            var dataLines = lines.Skip(hasHeader ? 1 : 0).Take(30).Select(l => SplitLine(l, delimiter)).ToList();
            roles = _options.HeaderlessColumns.Count > 0
                ? _options.HeaderlessColumns.Select(ParseRoleName).ToArray()
                : InferRoles(dataLines);
            columnsKey = (hasHeader ? string.Join(",", first.Select(NormalizeHeader)) + "|" : "") + string.Join(",", roles.Select(r => r.ToString().ToLowerInvariant()));
        }

        var fingerprint = $"{(hasHeader ? "H" : "N")}|{DelimiterName(delimiter)}|{columnsKey}";
        int isinCol = Array.IndexOf(roles, Role.Isin);
        int priceCol = Array.IndexOf(roles, Role.Price);
        var roleNames = roles.Select(r => r.ToString().ToLowerInvariant()).ToList();
        if (isinCol < 0 || priceCol < 0)
        {
            Warn("Spalten für ISIN und Preis nicht erkannt – Format unbekannt");
            return Finish([], delimiter, hasHeader, fingerprint, false, warnings, lines.Count - (hasHeader ? 1 : 0), false, roleNames);
        }

        var rows = lines.Skip(hasHeader ? 1 : 0).Select(l => SplitLine(l, delimiter)).ToList();
        bool decimalComma = delimiter != ',' && rows.Take(200).Any(r => priceCol < r.Length && r[priceCol].Contains(','));

        int Col(Role r) => Array.IndexOf(roles, r);
        int dtCol = Col(Role.DateTime), dateCol = Col(Role.Date), timeCol = Col(Role.Time), volCol = Col(Role.Volume),
            bidCol = Col(Role.Bid), askCol = Col(Role.Ask), bidSizeCol = Col(Role.BidSize), askSizeCol = Col(Role.AskSize), ccyCol = Col(Role.Currency);
        if (dtCol < 0 && timeCol < 0) Warn("Keine Zeitspalte erkannt");

        var trades = new List<GettexTrade>(rows.Count);
        int skipped = 0;
        int lineNo = hasHeader ? 1 : 0;
        foreach (var r in rows)
        {
            lineNo++;
            string? Get(int c) => c >= 0 && c < r.Length ? r[c].Trim().Trim('"').Trim() : null;
            var isin = Get(isinCol)?.ToUpperInvariant();
            if (!SymbolHelper.LooksLikeIsin(isin)) { skipped++; Warn($"Zeile {lineNo}: keine ISIN"); continue; }
            if (!TryParseNumber(Get(priceCol), decimalComma, out var price) || price <= 0) { skipped++; Warn($"Zeile {lineNo}: Preis unlesbar"); continue; }
            if (!TryParseTime(Get(dtCol), Get(dateCol), Get(timeCol), fileDate, out var timeUtc)) { skipped++; Warn($"Zeile {lineNo}: Zeit unlesbar"); continue; }
            decimal? Num(int c) => TryParseNumber(Get(c), decimalComma, out var v) ? v : null;
            trades.Add(new GettexTrade
            {
                Isin = isin!,
                TimeUtc = timeUtc,
                Price = price,
                Volume = Num(volCol) ?? 0m,
                Currency = Get(ccyCol) is { Length: 3 } ccy ? ccy.ToUpperInvariant() : "EUR",
                Bid = Num(bidCol),
                Ask = Num(askCol),
                BidSize = Num(bidSizeCol),
                AskSize = Num(askSizeCol),
            });
        }
        trades.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return Finish(trades, delimiter, hasHeader, fingerprint, true, warnings, skipped, decimalComma, roleNames);
    }

    private GettexParseResult Finish(List<GettexTrade> trades, char delimiter, bool hasHeader, string fingerprint, bool recognized,
        List<string> warnings, int skipped, bool decimalComma, IReadOnlyList<string> columns)
    {
        var previous = LastFingerprint;
        bool changed = previous is not null && previous != fingerprint;
        if (changed) warnings.Insert(0, $"Dateiformat geändert: vorher [{previous}], jetzt [{fingerprint}]");
        if (recognized) LastFingerprint = fingerprint;
        return new GettexParseResult
        {
            Trades = trades,
            Delimiter = delimiter,
            HasHeader = hasHeader,
            Fingerprint = fingerprint,
            FormatRecognized = recognized,
            FormatChanged = changed,
            PreviousFingerprint = previous,
            DecimalComma = decimalComma,
            SkippedLines = skipped,
            Warnings = warnings,
            Columns = columns,
        };
    }

    private static GettexParseResult Result(List<GettexTrade> trades, char delimiter, bool hasHeader, string fp, bool recognized, List<string> warnings, int skipped, bool comma, IReadOnlyList<string> cols)
        => new()
        {
            Trades = trades, Delimiter = delimiter, HasHeader = hasHeader, Fingerprint = fp, FormatRecognized = recognized,
            Warnings = warnings, SkippedLines = skipped, DecimalComma = comma, Columns = cols,
        };

    private static string DelimiterName(char c) => c switch { ';' => "semicolon", ',' => "comma", '\t' => "tab", '|' => "pipe", _ => c.ToString() };

    private static Role ParseRoleName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "isin" => Role.Isin, "datetime" => Role.DateTime, "date" => Role.Date, "time" => Role.Time, "price" => Role.Price,
        "volume" => Role.Volume, "bid" => Role.Bid, "ask" => Role.Ask, "bidsize" => Role.BidSize, "asksize" => Role.AskSize,
        "currency" => Role.Currency, _ => Role.Ignore
    };

    /// <summary>Doppelte Rollen: nur die erste Spalte zählt (z. B. "Kurs" und "Preis").</summary>
    private static Role[] DeduplicateRoles(Role[] roles)
    {
        var seen = new HashSet<Role>();
        return roles.Select(r => r == Role.Ignore || seen.Add(r) ? r : Role.Ignore).ToArray();
    }

    // ---------- Trennzeichen und Felder ----------

    internal static char DetectDelimiter(IReadOnlyList<string> sample)
    {
        char best = ';';
        double bestScore = -1;
        foreach (var c in new[] { ';', '\t', ',', '|' })
        {
            var counts = sample.Select(l => CountOutsideQuotes(l, c)).ToList();
            if (counts.Count == 0 || counts.Min() == 0) continue;
            // Gleichmäßige Anzahl je Zeile spricht für das Trennzeichen.
            bool consistent = counts.Distinct().Count() == 1;
            double score = counts.Average() + (consistent ? 1000 : 0);
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        int n = 0;
        bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == c && !q) n++;
        }
        return n;
    }

    internal static string[] SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (q && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else q = !q;
            }
            else if (ch == delimiter && !q)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    // ---------- Spalten ohne Kopfzeile erraten ----------

    private Role[] InferRoles(List<string[]> rows)
    {
        int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        var roles = new Role[cols];
        double Ratio(int c, Func<string, bool> pred)
        {
            var vals = rows.Where(r => c < r.Length).Select(r => r[c].Trim().Trim('"')).Where(v => v.Length > 0).ToList();
            return vals.Count == 0 ? 0 : vals.Count(pred) / (double)vals.Count;
        }
        bool Free(int c) => roles[c] == Role.Ignore;
        void AssignFirst(Role role, Func<string, bool> pred)
        {
            for (int c = 0; c < cols; c++)
                if (Free(c) && Ratio(c, pred) >= 0.8) { roles[c] = role; return; }
        }

        AssignFirst(Role.Isin, v => SymbolHelper.LooksLikeIsin(v.ToUpperInvariant()));
        AssignFirst(Role.DateTime, v => TryParseDateTimeLocal(v, out _) || (HasExplicitOffset(v) && v.Length >= 10 && JsonRead.TryParseIsoUtc(v, out _)));
        AssignFirst(Role.Date, v => TryParseDate(v, out _));
        AssignFirst(Role.Time, v => TryParseTimeOfDay(v, out _));
        AssignFirst(Role.Currency, v => v.Length == 3 && v.All(char.IsAsciiLetterUpper));
        // Zahlen: erste = Preis, zweite = Volumen.
        var numeric = Enumerable.Range(0, cols).Where(c => Free(c) && Ratio(c, v => TryParseNumberLoose(v, out _)) >= 0.8).ToList();
        if (numeric.Count > 0) roles[numeric[0]] = Role.Price;
        if (numeric.Count > 1) roles[numeric[1]] = Role.Volume;
        return roles;
    }

    // ---------- Zahlen ----------

    private static bool TryParseNumberLoose(string? s, out decimal value)
        => TryParseNumber(s, s is not null && s.Contains(',') && !s.Contains('.'), out value);

    /// <summary>Zahl mit Dezimalkomma (de-DE, Punkt als Tausendertrenner) oder Dezimalpunkt.</summary>
    internal static bool TryParseNumber(string? raw, bool decimalComma, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Trim('"').Replace(" ", "").Replace(" ", "").Replace("'", "");
        if (s.Length == 0) return false;
        if (decimalComma)
        {
            if (s.Contains(','))
                s = s.Replace(".", "").Replace(',', '.');
            else if (s.Contains('.'))
            {
                // "12.5" in einer Dezimalkomma-Datei: Punkt nur dann Tausendertrenner, wenn genau 3 Stellen folgen.
                var parts = s.Split('.');
                if (parts.Length > 2 || parts[^1].Length == 3) s = s.Replace(".", "");
            }
        }
        else
        {
            if (s.Contains(',') && s.Contains('.')) s = s.Replace(",", "");
            else if (s.Contains(',')) s = s.Replace(',', '.');
        }
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ---------- Zeiten ----------

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm",
        "dd.MM.yyyy HH:mm:ss.FFFFFFF", "dd.MM.yyyy HH:mm", "d.M.yyyy HH:mm:ss.FFFFFFF",
        "yyyyMMdd HH:mm:ss.FFFFFFF", "yyyyMMdd'T'HHmmss.FFFFFFF", "yyyyMMddHHmmss", "yyyyMMdd HHmmss",
        "dd/MM/yyyy HH:mm:ss.FFFFFFF", "yyyy/MM/dd HH:mm:ss.FFFFFFF",
    ];
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyyMMdd", "dd/MM/yyyy", "yyyy/MM/dd", "dd.MM.yy"];
    private static readonly string[] TimeFormats = ["HH:mm:ss.FFFFFFF", "H:mm:ss.FFFFFFF", "HH:mm", "HHmmss", "HHmmssfff"];

    private static string TrimFraction(string s)
    {
        var dot = s.LastIndexOf('.');
        if (dot < 0 || dot < s.LastIndexOf(':')) return s;
        int end = dot + 1;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        return end - dot - 1 > 7 ? s[..(dot + 8)] + s[end..] : s;
    }

    private static bool HasExplicitOffset(string s)
    {
        if (s.EndsWith('Z') || s.EndsWith('z')) return true;
        var t = s.LastIndexOfAny(['T', ' ']);
        if (t < 0) return false;
        var tail = s[(t + 1)..];
        return tail.Contains('+') || (tail.LastIndexOf('-') > 0);
    }

    internal static bool TryParseDateTimeLocal(string s, out DateTime local)
        => DateTime.TryParseExact(TrimFraction(s.Trim()), DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out local);

    internal static bool TryParseDate(string s, out DateOnly date)
        => DateOnly.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal static bool TryParseTimeOfDay(string s, out TimeOnly time)
    {
        var t = TrimFraction(s.Trim());
        if (DateTime.TryParseExact(t, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            time = TimeOnly.FromTimeSpan(dt.TimeOfDay);
            return true;
        }
        time = default;
        return false;
    }

    private DateTimeOffset LocalToUtc(DateTime local)
        => _options.AssumeUtcWhenNoOffset
            ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero)
            : BerlinTime.ToUtc(local);

    internal bool TryParseTime(string? dateTime, string? date, string? time, DateOnly? fileDate, out DateTimeOffset utc)
    {
        utc = default;
        foreach (var candidate in new[] { dateTime, time })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var s = candidate.Trim();
            if (HasExplicitOffset(s) && s.Length >= 10 && JsonRead.TryParseIsoUtc(s, out utc)) return true;
            if (TryParseDateTimeLocal(s, out var local)) { utc = LocalToUtc(local); return true; }
        }
        if (!string.IsNullOrWhiteSpace(time) && TryParseTimeOfDay(time, out var tod))
        {
            DateOnly? d = !string.IsNullOrWhiteSpace(date) && TryParseDate(date, out var dd) ? dd : fileDate;
            d ??= DateOnly.FromDateTime(BerlinTime.FromUtc(DateTimeOffset.UtcNow));
            utc = LocalToUtc(d.Value.ToDateTime(tod));
            return true;
        }
        return false;
    }
}
