using System.Text;
using Clircs;
using Clircs.ConsoleClient;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine(ProductInfo.DisplayName);
    return;
}

await using var application = new ClientApplication();
await application.RunAsync();
