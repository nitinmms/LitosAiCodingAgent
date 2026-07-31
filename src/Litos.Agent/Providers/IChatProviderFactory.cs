namespace Litos.Agent.Providers;

public interface IChatProviderFactory
{
    IChatProvider Resolve(string providerName);
}
