using System.IdentityModel.Tokens.Jwt;
using Litos.Api.Auth;
using Litos.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Litos.Api.Tests.Auth;

public class JwtTokenServiceTests
{
    private const string SigningKey = "unit-test-signing-key-at-least-32-bytes-long!!";

    private static (LitosDbContext Db, JwtTokenService Tokens) CreateService()
    {
        // Same in-memory-database approach as UserStoreTests — no external Postgres dependency.
        var options = new DbContextOptionsBuilder<LitosDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        var db = new LitosDbContext(options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT_SIGNING_KEY"] = SigningKey })
            .Build();
        var signingKeyProvider = new JwtSigningKeyProvider(configuration);

        return (db, new JwtTokenService(db, signingKeyProvider));
    }

    private static User NewUser(bool isAdmin = false) => new()
    {
        Id = Guid.NewGuid(),
        Username = "alice",
        PasswordHash = "irrelevant-for-these-tests",
        IsAdmin = isAdmin,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task IssueAsync_ReturnsAccessTokenCarryingUserIdClaim()
    {
        var (_, tokens) = CreateService();
        var user = NewUser();

        var pair = await tokens.IssueAsync(user, CancellationToken.None);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(c => c.Type == AdminAuthEndpoints.UserIdClaimType).Value);
    }

    [Fact]
    public async Task IssueAsync_NonAdminUser_AccessTokenHasNoAdminClaim()
    {
        var (_, tokens) = CreateService();
        var user = NewUser(isAdmin: false);

        var pair = await tokens.IssueAsync(user, CancellationToken.None);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == AdminAuthEndpoints.AdminClaimType);
    }

    [Fact]
    public async Task IssueAsync_AdminUser_AccessTokenCarriesAdminClaim()
    {
        var (_, tokens) = CreateService();
        var user = NewUser(isAdmin: true);

        var pair = await tokens.IssueAsync(user, CancellationToken.None);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        Assert.Contains(jwt.Claims, c => c.Type == AdminAuthEndpoints.AdminClaimType);
    }

    [Fact]
    public async Task RefreshAsync_ValidToken_ReturnsNewPairWithDifferentRefreshToken()
    {
        // Asserts on the refresh token, not the access token: the access JWT's exp/iat claims
        // are second-granularity, so two tokens issued within the same test run can come out
        // byte-identical even though they're independently minted — not a meaningful signal of
        // "did refresh actually happen." The refresh token (32 random bytes per call) always
        // differs, which is what actually proves rotation occurred.
        var (db, tokens) = CreateService();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        var issued = await tokens.IssueAsync(user, CancellationToken.None);

        var refreshed = await tokens.RefreshAsync(issued.RefreshToken, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.NotEqual(issued.RefreshToken, refreshed!.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_TokenIsRotated_OldRefreshTokenNoLongerWorks()
    {
        var (db, tokens) = CreateService();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        var issued = await tokens.IssueAsync(user, CancellationToken.None);

        await tokens.RefreshAsync(issued.RefreshToken, CancellationToken.None);
        var reuseAttempt = await tokens.RefreshAsync(issued.RefreshToken, CancellationToken.None);

        Assert.Null(reuseAttempt);
    }

    [Fact]
    public async Task RefreshAsync_UnknownToken_ReturnsNull()
    {
        var (_, tokens) = CreateService();

        var result = await tokens.RefreshAsync("not-a-real-token", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshAsync_UserDisabledSinceIssue_ReturnsNull()
    {
        var (db, tokens) = CreateService();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        var issued = await tokens.IssueAsync(user, CancellationToken.None);

        user.DisabledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await tokens.RefreshAsync(issued.RefreshToken, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_ThenRefresh_NoLongerWorks()
    {
        var (db, tokens) = CreateService();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        var issued = await tokens.IssueAsync(user, CancellationToken.None);

        await tokens.RevokeAllForUserAsync(user.Id, CancellationToken.None);
        var result = await tokens.RefreshAsync(issued.RefreshToken, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Constructor_SigningKeyTooShort_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT_SIGNING_KEY"] = "too-short" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new JwtSigningKeyProvider(configuration));
    }

    [Fact]
    public void Constructor_NoSigningKeyConfigured_Throws()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        Assert.Throws<InvalidOperationException>(() => new JwtSigningKeyProvider(configuration));
    }
}
