using System.Security.Claims;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>A change to a settings section: the revision it was read at, and the section in full.</summary>
public sealed record SaveSettingsRequest<T>(long Revision, T? Settings);

public sealed record SetSecretRequest(string? Value);

/// <summary>
/// Factory settings, for Admins only (m3-architecture.md §3.4). Each section is read and written
/// whole, with the revision it was read at; a secret is set or cleared, never shown.
/// </summary>
public static class SettingsApi
{
    public static IEndpointRouteBuilder MapFactorySettings(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(FactoryRoles.Admin);

        admin.MapGet("/settings", async (FactorySettings settings, CancellationToken ct) => Results.Ok(new
        {
            Budgets = Section(settings, SettingsSections.Budgets, settings.Budgets),
            Secrets = await settings.SecretsAsync(ct),
        }));

        admin.MapPut("/settings/budgets", async (
            SaveSettingsRequest<BudgetSettings> request, ClaimsPrincipal user, FactorySettings settings, FactorySignals signals, CancellationToken ct) =>
        {
            if (request.Settings is not { } budgets)
                return Results.BadRequest(new { error = "settings are required." });
            if (budgets.Validate() is { Count: > 0 } errors)
                return Results.BadRequest(new { error = string.Join(" ", errors), errors });

            var saved = await SaveAsync(settings, SettingsSections.Budgets, budgets, request.Revision, user, ct);
            // A higher slot cap may let a waiting run start now.
            signals.WorkQueued();
            return saved;
        });

        admin.MapPut("/secrets/{name}", async (string name, SetSecretRequest request, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
        {
            if (!SecretNames.IsValid(name))
                return Results.BadRequest(new { error = $"\"{name}\" is not a secret the factory keeps." });
            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new { error = "The value is empty. To remove a secret, clear it." });

            await settings.SetSecretAsync(name, request.Value.Trim(), user.UserId(), ct);
            return Results.NoContent();
        });

        admin.MapDelete("/secrets/{name}", async (string name, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
            await settings.ClearSecretAsync(name, user.UserId(), ct) ? Results.NoContent() : Results.NotFound(new { error = "That secret is not set." }));

        return app;
    }

    private static object Section<T>(FactorySettings settings, string section, T value) => new { Revision = settings.RevisionOf(section), Settings = value };

    private static async Task<IResult> SaveAsync<T>(
        FactorySettings settings, string section, T value, long revision, ClaimsPrincipal user, CancellationToken ct) where T : class
    {
        try
        {
            var saved = await settings.SaveAsync(section, value, revision, user.UserId(), ct);
            return Results.Ok(new { Revision = saved, Settings = value });
        }
        catch (StoreConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
