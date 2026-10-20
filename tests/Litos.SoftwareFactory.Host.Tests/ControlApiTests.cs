using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Host.Api;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The installed launcher's control endpoints (SoftwareFactoryInstallation.md §4.1): stop, and the first sign-in's status.</summary>
public sealed class ControlApiTests
{
    private const string Token = "a-control-token";

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Add(ControlApi.TokenHeader, token);
        return request;
    }

    [Fact]
    public async Task Stop_WithTheToken_StopsTheHost()
    {
        await using var host = await TestHost.StartAsync(o => o.ControlToken = Token, signIn: false);

        using var response = await host.NewClient().SendAsync(Request(HttpMethod.Post, "control/stop", Token));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(host.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-control-tokeN")]
    [InlineData("a-control-token-and-more")]
    public async Task Stop_WithoutTheRightToken_IsNotFound_AndTheHostRuns(string? token)
    {
        await using var host = await TestHost.StartAsync(o => o.ControlToken = Token, signIn: false);

        using var response = await host.NewClient().SendAsync(Request(HttpMethod.Post, "control/stop", token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(host.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task WithoutAControlToken_ThereAreNoControlEndpoints()
    {
        await using var host = await TestHost.StartAsync(signIn: false);

        // Not even an empty token opens them.
        using var response = await host.NewClient().SendAsync(Request(HttpMethod.Post, "control/stop", ""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(host.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task Status_SaysWhetherTheSeededAdminMustStillChangeTheirPassword()
    {
        await using var host = await TestHost.StartAsync(o => { o.ControlToken = Token; o.AdminPasswordOneTime = true; });
        var launcher = host.NewClient();

        Assert.True(await MustChangeAsync(launcher));

        using (var changed = await host.Client.PostAsJsonAsync("api/auth/password", new { currentPassword = TestHost.AdminPassword, newPassword = "a-new-long-password" }))
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.False(await MustChangeAsync(launcher));

        static async Task<bool> MustChangeAsync(HttpClient client)
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, "control/status", Token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("adminMustChangePassword").GetBoolean();
        }
    }
}
