using Litos.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Litos.Api.Tests.Data;

public class UserStoreTests
{
    private static UserStore CreateStore()
    {
        // A fresh in-memory database per test (unique Guid-named), not Npgsql — keeps this suite
        // free of an external Postgres dependency, matching how the rest of Litos.Api.Tests has
        // no external services. Real-Postgres behavior (migrations, actual SQL) is exercised
        // manually against docker-compose's postgres service, not here.
        var options = new DbContextOptionsBuilder<LitosDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        return new UserStore(new LitosDbContext(options));
    }

    [Fact]
    public async Task CreateAsync_ThenFindByUsername_ReturnsTheUser()
    {
        var store = CreateStore();

        var created = await store.CreateAsync("alice", "correct-horse-battery-staple", isAdmin: false, CancellationToken.None);
        var found = await store.FindByUsernameAsync("alice", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
        Assert.False(found.IsAdmin);
        Assert.Null(found.DisabledAt);
    }

    [Fact]
    public async Task CreateAsync_DoesNotStorePasswordInPlainText()
    {
        var store = CreateStore();
        const string password = "correct-horse-battery-staple";

        var user = await store.CreateAsync("alice", password, isAdmin: false, CancellationToken.None);

        Assert.NotEqual(password, user.PasswordHash);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_CorrectPassword_ReturnsUser()
    {
        var store = CreateStore();
        await store.CreateAsync("alice", "correct-horse-battery-staple", isAdmin: false, CancellationToken.None);

        var result = await store.ValidateCredentialsAsync("alice", "correct-horse-battery-staple", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("alice", result!.Username);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_WrongPassword_ReturnsNull()
    {
        var store = CreateStore();
        await store.CreateAsync("alice", "correct-horse-battery-staple", isAdmin: false, CancellationToken.None);

        var result = await store.ValidateCredentialsAsync("alice", "wrong-password", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_UnknownUsername_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.ValidateCredentialsAsync("nobody", "anything", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_DisabledUser_ReturnsNullEvenWithCorrectPassword()
    {
        var store = CreateStore();
        var user = await store.CreateAsync("alice", "correct-horse-battery-staple", isAdmin: false, CancellationToken.None);
        await store.SetDisabledAsync(user.Id, disabled: true, CancellationToken.None);

        var result = await store.ValidateCredentialsAsync("alice", "correct-horse-battery-staple", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SetDisabledAsync_ThenReEnable_AllowsLoginAgain()
    {
        var store = CreateStore();
        var user = await store.CreateAsync("alice", "correct-horse-battery-staple", isAdmin: false, CancellationToken.None);
        await store.SetDisabledAsync(user.Id, disabled: true, CancellationToken.None);

        await store.SetDisabledAsync(user.Id, disabled: false, CancellationToken.None);
        var result = await store.ValidateCredentialsAsync("alice", "correct-horse-battery-staple", CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCreatedUsersOrderedByUsername()
    {
        var store = CreateStore();
        await store.CreateAsync("bob", "password-bob-123", isAdmin: false, CancellationToken.None);
        await store.CreateAsync("alice", "password-alice-123", isAdmin: true, CancellationToken.None);

        var users = await store.ListAsync(CancellationToken.None);

        Assert.Equal(["alice", "bob"], users.Select(u => u.Username));
    }
}
