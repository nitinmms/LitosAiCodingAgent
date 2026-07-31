using Litos.Agent.Tools;
using Litos.Host.Tests.Fakes;
using Litos.Tools.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.Host.Tests;

public class LitosHostBuilderTests
{
    [Fact]
    public void AddLitosAgent_RegistersSearchCode_AlongsideShell_InToolSchemas()
    {
        var config = new LitosConfig(DefaultProvider: "anthropic", DefaultModel: null, LastWorkingDirectory: null, ApiKeys: new Dictionary<string, string>());
        var services = new ServiceCollection().AddLitosAgent(config);
        services.AddSingleton<IToolApprovalGate, FakeApprovalGate>();
        var provider = services.BuildServiceProvider();

        var toolNames = provider.GetRequiredService<ToolRegistryFactory>().Create().Schemas.Select(s => s.Name);

        Assert.Contains("search_code", toolNames);
        Assert.Contains("shell", toolNames);
        Assert.Contains("web_search", toolNames);
    }
}
