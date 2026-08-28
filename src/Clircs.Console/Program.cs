using Clircs;
using Clircs.ConsoleClient;

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(ProductInfo.DisplayName);
    return;
}

await using var application = new ClientApplication();
await application.RunAsync();
