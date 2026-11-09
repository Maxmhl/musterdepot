// Zeichnet echte Krypto-Daten für Golden Replays/Backtests auf und erzeugt/prüft Testdaten. Siehe docs/Tests.md.
using Musterdepot.Recorder.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cts.IsCancellationRequested) return; // zweites Strg+C beendet sofort
    e.Cancel = true;
    Console.Error.WriteLine("Beende Aufzeichnung … (erneut Strg+C zum sofortigen Abbruch)");
    cts.Cancel();
};

return await RecorderCli.RunAsync(args, Console.Out, Console.Error, cts.Token);
