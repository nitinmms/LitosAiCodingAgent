using System.Security.AccessControl;
using System.Security.Principal;

namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>factory.env: the generator that replaces new-env.py (G5), and edits that keep the rest of the file.</summary>
public sealed class FactoryEnvTests
{
    [Fact]
    public void ANewInstall_GetsGeneratedPasswords_TheChosenPorts_AndTheInstallerSettings()
    {
        var env = FactoryEnv.CreateNew(@"C:\Users\someone\AppData\Local\LitosSoftwareFactory-Installed", hostPort: 5181, dbPort: 5434);

        var runtime = env["FACTORY_DB_RUNTIME_PASSWORD"];
        var migrator = env["FACTORY_DB_MIGRATOR_PASSWORD"];
        Assert.Equal($"Host=127.0.0.1;Port=5434;Database=litos_factory;Username=factory_runtime;Password={runtime}", env["ConnectionStrings__FactoryState"]);
        Assert.Equal($"Host=127.0.0.1;Port=5434;Database=litos_factory;Username=factory_migrator;Password={migrator}", env["ConnectionStrings__FactoryMigrations"]);
        Assert.Equal("5434", env[FactoryEnv.DbPort]);
        Assert.Equal("http://127.0.0.1:5181", env.Url);
        Assert.Equal("C:/Users/someone/AppData/Local/LitosSoftwareFactory-Installed", env.DataDirectory);
        Assert.Equal("C:/Users/someone/AppData/Local/LitosSoftwareFactory-Installed/logs", env[FactoryEnv.LogDir]);
        Assert.Equal("admin", env[FactoryEnv.AdminUser]);
        Assert.Equal("1", env["FACTORY_ADMIN_PASSWORD_ONE_TIME"]);
        Assert.Equal(StartDockerPolicy.Ask, env.StartDockerPolicy);
    }

    [Fact]
    public void EverySecret_IsDifferent_UrlSafe_AndLongEnoughForTheAdminPasswordRules()
    {
        var env = FactoryEnv.CreateNew("C:/data", 5180, 5433);
        string[] keys = ["FACTORY_DB_ADMIN_PASSWORD", "FACTORY_DB_MIGRATOR_PASSWORD", "FACTORY_DB_RUNTIME_PASSWORD", FactoryEnv.AdminPassword, FactoryEnv.ControlToken];

        var secrets = keys.Select(key => env[key]!).ToList();

        Assert.Equal(keys.Length, secrets.Distinct().Count());
        Assert.All(secrets, secret =>
        {
            // Identity requires 12 characters; ';' or '=' would break the connection strings.
            Assert.Equal(32, secret.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", secret);
        });
    }

    [Fact]
    public void Set_ReplacesAValueInPlace_AndKeepsCommentsAndOtherLines()
    {
        var env = FactoryEnv.Parse("# mine\nA=1\nFACTORY_START_DOCKER=ask\r\nB=2\n");

        env.Set(FactoryEnv.StartDocker, "always");
        env.Set("C", "3");

        Assert.Equal("# mine\nA=1\nFACTORY_START_DOCKER=always\nB=2\nC=3\n", env.ToString());
        Assert.Equal(StartDockerPolicy.Always, env.StartDockerPolicy);
    }

    [Theory]
    [InlineData("FACTORY_START_DOCKER=always", StartDockerPolicy.Always)]
    [InlineData("FACTORY_START_DOCKER= Never ", StartDockerPolicy.Never)]
    [InlineData("FACTORY_START_DOCKER=ask", StartDockerPolicy.Ask)]
    [InlineData("FACTORY_START_DOCKER=sometimes", StartDockerPolicy.Ask)]
    [InlineData("", StartDockerPolicy.Ask)]
    public void TheStartDockerSetting_IsAskUnlessItSaysOtherwise(string line, StartDockerPolicy expected) =>
        Assert.Equal(expected, FactoryEnv.Parse(line).StartDockerPolicy);

    [Fact]
    public void Values_AreReadAsTheHostReadsThem()
    {
        var env = FactoryEnv.Parse("# FACTORY_DATA_DIR=commented\n factory_data_dir = \"C:/quoted\" \nASPNETCORE_URLS=http://127.0.0.1:5199/;http://localhost:5199\n");

        Assert.Equal("C:/quoted", env.DataDirectory);
        Assert.Equal("http://127.0.0.1:5199", env.Url);
        Assert.Null(env["MISSING"]);
    }

    [Fact]
    public void Save_WritesAFileOnlyItsOwnerCanRead()
    {
        var folder = Directory.CreateTempSubdirectory("litos-setup-env-").FullName;
        try
        {
            var path = Path.Combine(folder, "factory.env");
            FactoryEnv.CreateNew("C:/data", 5180, 5433).Save(path);

            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

            Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
            var rule = Assert.Single(rules);
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.Equal(FactoryEnv.Load(path).ToString(), File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
