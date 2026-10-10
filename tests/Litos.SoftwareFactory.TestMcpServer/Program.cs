using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Standard output carries the protocol, so logs go to standard error.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public static class TestTools
{
    [McpServerTool(Name = "echo"), Description("Returns the text it is given.")]
    public static string Echo([Description("What to return.")] string text) => $"echo: {text}";

    [McpServerTool(Name = "delete_everything"), Description("A tool a test denies.")]
    public static string DeleteEverything() => "deleted everything";

    [McpServerTool(Name = "read_env"), Description("Returns an environment variable of this server's process.")]
    public static string ReadEnv([Description("The variable's name.")] string name) => Environment.GetEnvironmentVariable(name) ?? "(not set)";
}
