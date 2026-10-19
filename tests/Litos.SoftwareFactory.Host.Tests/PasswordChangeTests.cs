using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Changing a password, and the one-time Admin password an installer shows once (SoftwareFactoryInstallation.md §4.4).</summary>
public sealed class PasswordChangeTests
{
    private const string NewPassword = "a-new-long-password";

    private static async Task<JsonElement> MeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("api/auth/me"));

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string current, string next) =>
        client.PostAsJsonAsync("api/auth/password", new { currentPassword = current, newPassword = next });

    [Fact]
    public async Task AOneTimeAdminPassword_AllowsNothingButChoosingANewOne()
    {
        await using var host = await TestHost.StartAsync(o => o.AdminPasswordOneTime = true);

        Assert.True((await MeAsync(host.Client)).GetProperty("mustChangePassword").GetBoolean());
        using (var blocked = await host.Client.GetAsync("api/projects"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            var body = await blocked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(("Choose a new password before going on.", true), (body.GetProperty("error").GetString(), body.GetProperty("mustChangePassword").GetBoolean()));
        }

        using (var changed = await ChangeAsync(host.Client, TestHost.AdminPassword, NewPassword))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.False((await MeAsync(host.Client)).GetProperty("mustChangePassword").GetBoolean());
        using (var allowed = await host.Client.GetAsync("api/projects"))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // The old password no longer signs in; the new one does.
        var fresh = host.NewClient();
        using (var old = await fresh.PostAsJsonAsync("api/auth/login", new { userName = "admin", password = TestHost.AdminPassword }))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        await host.SignInAsync(fresh, password: NewPassword);
        Assert.False((await MeAsync(fresh)).GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task ByDefault_TheSeededAdminNeedNotChangeTheirPassword()
    {
        await using var host = await TestHost.StartAsync();

        Assert.False((await MeAsync(host.Client)).GetProperty("mustChangePassword").GetBoolean());
        using var response = await host.Client.GetAsync("api/projects");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("not-the-password", NewPassword, "The current password is incorrect.")]
    [InlineData(TestHost.AdminPassword, TestHost.AdminPassword, "The new password must differ from the current one.")]
    [InlineData(TestHost.AdminPassword, "short", "Passwords must be at least 12 characters.")]
    [InlineData("", NewPassword, "Give the current password and a new one.")]
    public async Task AChangeThatCannotBeMade_SaysWhy_AndChangesNothing(string current, string next, string reason)
    {
        await using var host = await TestHost.StartAsync();

        using var response = await ChangeAsync(host.Client, current, next);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        await host.SignInAsync(host.NewClient());
    }

    [Fact]
    public async Task AMember_ChangesTheirOwnPassword()
    {
        await using var host = await TestHost.StartAsync();
        await host.CreateMemberAsync("mia");
        var mia = await host.SignedInAsync("mia");

        using (var changed = await ChangeAsync(mia, TestHost.MemberPassword, NewPassword))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        await host.SignInAsync(host.NewClient(), "mia", NewPassword);
    }

    [Fact]
    public async Task WithoutTheCsrfHeader_NoPasswordIsChanged()
    {
        await using var host = await TestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Remove(Auth.FactoryAuth.CsrfHeader);

        using var response = await ChangeAsync(host.Client, TestHost.AdminPassword, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await host.SignInAsync(host.NewClient());
    }
}
