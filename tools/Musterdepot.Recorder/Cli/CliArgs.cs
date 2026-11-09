using System.Globalization;

namespace Musterdepot.Recorder.Cli;

/// <summary>Fehler in den Befehlszeilenargumenten (Exitcode 1, Hilfe-Hinweis).</summary>
public sealed class CliException(string message) : Exception(message);

/// <summary>
/// Schlanker Argument-Parser ohne Fremdabhängigkeit: Positionsargumente, <c>--name wert</c>, <c>--name=wert</c>
/// und Schalter (<c>--force</c>). Schalter müssen beim Parsen bekannt sein, damit <c>--force datei</c> eindeutig bleibt.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options;

    private CliArgs(List<string> positionals, Dictionary<string, string?> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public IReadOnlyList<string> Positionals { get; }
    public IReadOnlyCollection<string> OptionNames => _options.Keys;

    public static CliArgs Parse(IEnumerable<string> args, IEnumerable<string>? flags = null)
    {
        var flagSet = new HashSet<string>(flags ?? [], StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a == "--")
            {
                positionals.AddRange(list.Skip(i + 1));
                break;
            }
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2)
            {
                positionals.Add(a);
                continue;
            }
            var body = a[2..];
            string name;
            string? value;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                name = body[..eq];
                value = body[(eq + 1)..];
            }
            else if (flagSet.Contains(body))
            {
                name = body;
                value = null;
            }
            else
            {
                name = body;
                if (i + 1 >= list.Count || list[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new CliException($"Option --{name} erwartet einen Wert.");
                value = list[++i];
            }
            if (!options.TryAdd(name, value))
                throw new CliException($"Option --{name} ist doppelt angegeben.");
        }
        return new CliArgs(positionals, options);
    }

    /// <summary>Wirft, wenn eine Option angegeben wurde, die der Befehl nicht kennt.</summary>
    public void EnsureOnly(params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (var name in _options.Keys)
            if (!set.Contains(name))
                throw new CliException($"Unbekannte Option --{name}. Erlaubt: {string.Join(", ", allowed.Select(x => "--" + x))}");
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public string Require(string name) =>
        Get(name) is { Length: > 0 } v ? v : throw new CliException($"Option --{name} fehlt.");

    public int GetInt(string name, int defaultValue)
    {
        var v = Get(name);
        if (v is null) return defaultValue;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new CliException($"Option --{name}: '{v}' ist keine ganze Zahl.");
    }

    public double GetDouble(string name, double defaultValue)
    {
        var v = Get(name);
        if (v is null) return defaultValue;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new CliException($"Option --{name}: '{v}' ist keine Zahl.");
    }

    /// <summary>Kommagetrennte Liste, leere Einträge werden entfernt.</summary>
    public IReadOnlyList<string> GetList(string name) =>
        Get(name) is { } v
            ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
}

/// <summary>Zeitangaben für <c>slice</c>: ISO-8601 (UTC angenommen) oder relativ zum ersten Ereignis (<c>+90s</c>, <c>+5m</c>, <c>+1h</c>).</summary>
public readonly record struct TimeArg(DateTimeOffset? Absolute, TimeSpan? Offset)
{
    public static TimeArg Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith('+'))
        {
            var unit = char.ToLowerInvariant(text[^1]);
            var num = text[1..^1];
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
                throw new CliException($"Ungültige relative Zeit '{text}' (Beispiele: +90s, +5m, +1h).");
            TimeSpan span = unit switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                _ => throw new CliException($"Ungültige Einheit in '{text}' (s, m oder h)."),
            };
            return new TimeArg(null, span);
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return new TimeArg(t, null);
        throw new CliException($"Ungültige Zeit '{text}' (ISO 8601, z. B. 2026-11-02T10:05:00Z, oder +5m).");
    }

    public DateTimeOffset Resolve(DateTimeOffset reference) => Absolute ?? reference + Offset!.Value;
}
