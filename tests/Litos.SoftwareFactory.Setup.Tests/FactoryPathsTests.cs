namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>Where an install keeps its data, beside a source-tree dev setup (§14.5).</summary>
public sealed class FactoryPathsTests
{
    [Fact]
    public void TheDataFolder_IsTheUsualOne_UnlessADevSetupAlreadyHasFilesThere()
    {
        var local = Directory.CreateTempSubdirectory("litos-setup-local-").FullName;
        try
        {
            var paths = new FactoryPaths(@"C:\install", @"C:\config", local);
            var usual = Path.Combine(local, FactoryPaths.ProductFolder);

            Assert.Equal((usual, false), paths.ChooseDataDirectory());

            // Empty: still free.
            Directory.CreateDirectory(usual);
            Assert.Equal((usual, false), paths.ChooseDataDirectory());

            // A dev setup's data (new-env.py uses this folder): the install goes beside it.
            Directory.CreateDirectory(Path.Combine(usual, "workspaces"));
            Assert.Equal((Path.Combine(local, FactoryPaths.BesideDevFolder), true), paths.ChooseDataDirectory());
            Assert.False(FactoryPaths.IsOurDataDirectory(usual));

            // An earlier install's own folder is its own again.
            File.WriteAllText(Path.Combine(usual, FactoryPaths.InstallMarker), "");
            Assert.Equal((usual, false), paths.ChooseDataDirectory());
            Assert.True(FactoryPaths.IsOurDataDirectory(usual));
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }
    }
}
