using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using WpfLocatorDoctor.Mcp;

if (args.Any(a => a is "--help" or "-h" or "--version"))
{
    Console.WriteLine(args.Contains("--version") ? ServerInfo.Version : ServerInfo.HelpText);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Standard output carries the MCP messages, so logs must go to standard error. Only warnings and errors, to keep it quiet.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = ServerInfo.Name, Title = "WPF Locator Doctor", Version = ServerInfo.Version };
        o.ServerInstructions = ServerInfo.Instructions;
    })
    .WithStdioServerTransport()
    .AddWpfLocatorDoctor();

await builder.Build().RunAsync();
