// Brücken-Exe für Claude Desktop: stdio <-> Named Pipe zum laufenden Musterdepot.
using Musterdepot.McpBridge;

BridgeRunner.Options options;
try
{
    options = BridgeRunner.ParseArgs(args);
}
catch (ArgumentException ex)
{
    if (ex.Message.Length > 0) Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(BridgeRunner.Usage);
    return BridgeRunner.ExitUsage;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
return await BridgeRunner.RunAsync(options, stdin, stdout, Console.Error, cts.Token);
