using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Musterdepot.Core.Serialization;
using Xunit.Sdk;

namespace Musterdepot.IntegrationTests.Infrastructure;

/// <summary>
/// Vergleich mit eingecheckten Golden-Dateien unter <c>tests/data/golden/&lt;name&gt;.json</c>.
/// <para>Aktualisieren: <c>UPDATE_GOLDEN=1 dotnet test tests/Musterdepot.IntegrationTests</c> – schreibt die Ist-Werte
/// als neue Golden-Dateien (danach Diff im Git prüfen und committen).</para>
/// <para>Bei Abweichung wird <c>&lt;name&gt;.actual.json</c> daneben geschrieben (per .gitignore ausgeschlossen).</para>
/// </summary>
public sealed class GoldenFile(string directory, bool? update = null)
{
    public static GoldenFile Default { get; } = new(TestPaths.GoldenDirectory);

    /// <summary>Gleiche Regeln wie überall (camelCase, Enums als Text, decimal verlustfrei), aber eingerückt und mit LF.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonDefaults.Options)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool UpdateRequested =>
        Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    public string Directory { get; } = directory;
    public bool UpdateMode { get; } = update ?? UpdateRequested;

    public string PathFor(string name) => System.IO.Path.Combine(Directory, name.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".json");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public void Verify<T>(string name, T actual) => VerifyText(name, Serialize(actual));

    public void VerifyText(string name, string actualText)
    {
        var path = PathFor(name);
        var actual = Normalize(actualText);
        var actualPath = System.IO.Path.ChangeExtension(path, ".actual.json");
        if (UpdateMode)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            if (File.Exists(actualPath)) File.Delete(actualPath);
            return;
        }
        if (!File.Exists(path))
        {
            WriteActual(actualPath, actual);
            throw new XunitException(
                $"Golden-Datei fehlt: {path}\nIst-Wert: {actualPath}\nAnlegen mit: UPDATE_GOLDEN=1 dotnet test tests/Musterdepot.IntegrationTests");
        }
        var expected = Normalize(File.ReadAllText(path));
        if (expected == actual)
        {
            if (File.Exists(actualPath)) File.Delete(actualPath);
            return;
        }
        WriteActual(actualPath, actual);
        throw new XunitException(DescribeDifference(path, actualPath, expected, actual));
    }

    private static void WriteActual(string path, string text)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public static string Normalize(string text)
    {
        var t = text.Replace("\r\n", "\n").TrimEnd('\n');
        return t + "\n";
    }

    private static string DescribeDifference(string path, string actualPath, string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i]) i++;
        var sb = new StringBuilder();
        sb.AppendLine($"Abweichung von der Golden-Datei {path} ab Zeile {i + 1}:");
        sb.AppendLine($"  erwartet: {(i < e.Length ? e[i].Trim() : "<Dateiende>")}");
        sb.AppendLine($"  ist:      {(i < a.Length ? a[i].Trim() : "<Dateiende>")}");
        sb.AppendLine($"Ist-Datei: {actualPath}");
        sb.Append("Gewollte Änderung? UPDATE_GOLDEN=1 dotnet test tests/Musterdepot.IntegrationTests und Diff committen.");
        return sb.ToString();
    }
}
