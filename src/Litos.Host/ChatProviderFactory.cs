using Litos.Agent.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.Host;

public sealed class ChatProviderFactory(IServiceProvider services) : IChatProviderFactory
{
    public IChatProvider Resolve(string providerName) =>
        services.GetRequiredKeyedService<IChatProvider>(providerName);
}
