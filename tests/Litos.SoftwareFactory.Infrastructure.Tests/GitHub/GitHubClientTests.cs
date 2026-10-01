using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Infrastructure.GitHub;

namespace Litos.SoftwareFactory.Infrastructure.Tests.GitHub;

public class GitHubClientTests
{
    private static readonly PullRequestDraft Draft = new(
        "acme", "salesapp", "factory/7f3a-csv-export", "main", "Add CSV export for Orders", "Ready for human testing.");

    private static (GitHubClient Client, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new GitHubClient(GitHubClient.CreateHttpClient("ghp_token", handler)), handler);
    }

    [Fact]
    public async Task NoOpenPullRequest_CreatesADraft()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, "[]");
        handler.Enqueue(HttpStatusCode.Created, """{"number":212,"html_url":"https://github.com/acme/salesapp/pull/212"}""");

        var pr = await client.CreateOrUpdateDraftPullRequestAsync(Draft, default);

        Assert.Equal(new PullRequestRef(212, "https://github.com/acme/salesapp/pull/212"), pr);

        var lookup = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, lookup.Method);
        Assert.Equal("/repos/acme/salesapp/pulls", lookup.Uri.AbsolutePath);
        Assert.Contains("state=open", lookup.Uri.Query);
        Assert.Contains("head=acme%3Afactory%2F7f3a-csv-export", lookup.Uri.Query);

        var create = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal("/repos/acme/salesapp/pulls", create.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(create.Body!);
        Assert.True(body.RootElement.GetProperty("draft").GetBoolean()); // always a draft; humans merge
        Assert.Equal("factory/7f3a-csv-export", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Equal("Add CSV export for Orders", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Ready for human testing.", body.RootElement.GetProperty("body").GetString());
    }

    /// <summary>A rework run pushes to a branch that already has its PR.</summary>
    [Fact]
    public async Task OpenPullRequestExists_UpdatesItsTitleAndBody_InsteadOfCreatingASecond()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """[{"number":212,"html_url":"https://github.com/acme/salesapp/pull/212"}]""");
        handler.Enqueue(HttpStatusCode.OK, """{"number":212,"html_url":"https://github.com/acme/salesapp/pull/212"}""");

        var pr = await client.CreateOrUpdateDraftPullRequestAsync(Draft with { Body = "Rework: commas fixed." }, default);

        Assert.Equal(212, pr.Number);
        Assert.Equal(2, handler.Requests.Count);
        var update = handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, update.Method);
        Assert.Equal("/repos/acme/salesapp/pulls/212", update.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(update.Body!);
        Assert.Equal("Rework: commas fixed.", body.RootElement.GetProperty("body").GetString());
        Assert.False(body.RootElement.TryGetProperty("draft", out _));
        Assert.False(body.RootElement.TryGetProperty("base", out _)); // an update never retargets the PR
    }

    [Fact]
    public async Task Requests_CarryTheTokenAndGitHubsHeaders()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, "[]");
        handler.Enqueue(HttpStatusCode.Created, """{"number":1,"html_url":"https://github.com/acme/salesapp/pull/1"}""");

        await client.CreateOrUpdateDraftPullRequestAsync(Draft, default);

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer ghp_token", request.Headers["Authorization"]);
            Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
            Assert.Contains("application/vnd.github+json", request.Headers["Accept"]);
            Assert.Contains("litos-software-factory", request.Headers["User-Agent"]);
            Assert.Equal("api.github.com", request.Uri.Host);
        });
    }

    [Fact]
    public async Task GitHubRefuses_ThrowsWithGitHubsReason_AndTheStatus()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, "[]");
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed: head branch has no commits"}""");

        var ex = await Assert.ThrowsAsync<GitHubException>(() => client.CreateOrUpdateDraftPullRequestAsync(Draft, default));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("create the draft pull request", ex.Message);
        Assert.Contains("head branch has no commits", ex.Message);
        Assert.DoesNotContain("ghp_token", ex.Message);
    }

    [Fact]
    public async Task LookupForbidden_Throws_WithoutTryingToCreate()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by personal access token"}""");

        var ex = await Assert.ThrowsAsync<GitHubException>(() => client.CreateOrUpdateDraftPullRequestAsync(Draft, default));

        Assert.Equal(403, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ErrorBodyThatIsNotJson_StillGivesAUsableMessage()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html");

        var ex = await Assert.ThrowsAsync<GitHubException>(() => client.CreateOrUpdateDraftPullRequestAsync(Draft, default));

        Assert.Equal("GitHub refused to look up the pull request (HTTP 502).", ex.Message);
    }

    [Fact]
    public async Task ResponseWithoutAPullRequest_IsAnError()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, "[]");
        handler.Enqueue(HttpStatusCode.Created, "{}");

        await Assert.ThrowsAsync<GitHubException>(() => client.CreateOrUpdateDraftPullRequestAsync(Draft, default));
    }

    [Fact]
    public async Task OwnerAndRepository_AreEscapedInThePath()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, "[]");
        handler.Enqueue(HttpStatusCode.Created, """{"number":1,"html_url":"https://github.com/x/y/pull/1"}""");

        await client.CreateOrUpdateDraftPullRequestAsync(Draft with { Owner = "ac/me", Repository = "sales app" }, default);

        Assert.Equal("/repos/ac%2Fme/sales%20app/pulls", handler.Requests[0].Uri.AbsolutePath);
    }
}

public class GitHubRepositoryTests
{
    [Theory]
    [InlineData("https://github.com/nitinmms/filedb-sharp", "nitinmms", "filedb-sharp")]
    [InlineData("https://github.com/nitinmms/filedb-sharp.git", "nitinmms", "filedb-sharp")]
    [InlineData("https://github.com/nitinmms/filedb-sharp/", "nitinmms", "filedb-sharp")]
    [InlineData("  https://github.com/acme/sales.app  ", "acme", "sales.app")]
    [InlineData("git@github.com:acme/salesapp.git", "acme", "salesapp")]
    public void TryParse_GitHubUrls(string url, string owner, string name)
    {
        Assert.True(GitHubRepository.TryParse(url, out var repository));
        Assert.Equal(new GitHubRepository(owner, name), repository);
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/salesapp")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/acme/salesapp/tree/main")]
    [InlineData("http://github.com/acme/salesapp")]
    [InlineData("https://github.com.evil.example/acme/salesapp")]
    [InlineData("https://user:token@github.com/acme/salesapp")]
    [InlineData("")]
    public void TryParse_AnythingElse_IsRejected(string url)
    {
        Assert.False(GitHubRepository.TryParse(url, out var repository));
        Assert.Null(repository);
    }

    [Fact]
    public void Urls_AreDerivedFromOwnerAndName()
    {
        var repository = new GitHubRepository("acme", "salesapp");

        Assert.Equal("https://github.com/acme/salesapp.git", repository.CloneUrl);
        Assert.Equal("https://github.com/acme/salesapp", repository.WebUrl);
    }
}
