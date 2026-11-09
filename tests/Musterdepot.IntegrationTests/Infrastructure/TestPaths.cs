namespace Musterdepot.IntegrationTests.Infrastructure;

/// <summary>Pfade im Quellbaum (nicht im bin-Ordner), damit UPDATE_GOLDEN=1 direkt die eingecheckten Dateien schreibt.</summary>
public static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string DataDirectory => Path.Combine(RepoRoot, "tests", "data");
    public static string ReplaysDirectory => Path.Combine(DataDirectory, "replays");
    public static string GoldenDirectory => Path.Combine(DataDirectory, "golden");

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Musterdepot.slnx")))
                    return dir.FullName;
        throw new InvalidOperationException("Repository-Wurzel (Musterdepot.slnx) nicht gefunden.");
    }

    /// <summary>Neuer leerer temporärer Ordner, z. B. für Datenverzeichnis oder Rundlauf-Dateien.</summary>
    public static string NewTempDirectory(string prefix = "musterdepot-test")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDirectory(string dir)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
