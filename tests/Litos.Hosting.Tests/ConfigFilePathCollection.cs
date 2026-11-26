namespace Litos.Hosting.Tests;

/// <summary>
/// The tests that redirect <c>LitosConfig.ConfigFilePath</c>. It is a static, so any two tests that
/// change it must not run at the same time: xUnit parallelises by class, and a second class
/// redirecting it mid-test left the first writing into a file it did not own. Being in one
/// collection runs them one after another. Use this for any new test that sets that path.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ConfigFilePathCollection
{
    public const string Name = "LitosConfig.ConfigFilePath";
}