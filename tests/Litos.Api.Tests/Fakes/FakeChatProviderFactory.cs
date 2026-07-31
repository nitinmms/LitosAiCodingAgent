using Litos.Agent.Providers;

namespace Litos.Api.Tests.Fakes;

/// <summary>Resolves the same FakeChatProvider instance regardless of the requested provider name.</summary>
public sealed class FakeChatProviderFactory(FakeChatProvider provider) : IChatProviderFactory
{
    public IChatProvider Resolve(string providerName) => provider;
}
