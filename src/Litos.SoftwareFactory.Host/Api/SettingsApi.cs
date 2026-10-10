using System.Security.Claims;
using Litos.Agent.Providers;
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

        admin.MapGet("/settings", (FactorySettings settings) => Results.Ok(new
        {
            Budgets = Section(settings, SettingsSections.Budgets, settings.Budgets),
            Providers = Section(settings, SettingsSections.Providers, settings.Providers),
            Tools = Section(settings, SettingsSections.Tools, settings.Tools),
            Mcp = Section(settings, SettingsSections.Mcp, settings.Mcp),
            // What the Providers tab lists, in order, with what each needs to be called.
            KnownProviders = KnownProviders.All.Select(p => new
            {
                p.Name, p.DisplayName, Precision = p.Precision.ToString(), p.UsesBaseUrl, KeySecret = SecretNames.Provider(p.Name),
            }),
            Secrets = settings.Secrets,
        }));

        admin.MapPut("/settings/providers", async (
            SaveSettingsRequest<ProviderSettings> request, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
        {
            if (request.Settings is not { } providers)
                return Results.BadRequest(new { error = "settings are required." });
            if (providers.Validate() is { Count: > 0 } errors)
                return Results.BadRequest(new { error = string.Join(" ", errors), errors });

            return await SaveAsync(settings, SettingsSections.Providers, providers, request.Revision, user, ct);
        });

        // The context window of a model an Admin is about to allow: from OpenRouter's public
        // catalog, which lists the native providers' models too, or the engine's own table.
        admin.MapGet("/models/context-length", async (string? model, OpenRouterModelCatalog catalog, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(model)
                ? Results.BadRequest(new { error = "model is required." })
                : Results.Ok(new { Model = model.Trim(), ContextLength = await ModelContextResolver.ResolveAsync(catalog, model.Trim(), null, ct) }));

        // The catalog the Providers tab picks allowed models from (m3-architecture.md §4.3).
        admin.MapGet("/models/catalog", (FactorySettings settings, ModelCatalogService catalog) =>
            Results.Ok(new { Providers = KnownProviders.All.Select(p => CatalogView(p.Name, catalog.Current, settings.Providers)) }));

        admin.MapPost("/models/catalog/{provider}/refresh", async (string provider, FactorySettings settings, ModelCatalogService catalog, CancellationToken ct) =>
        {
            if (KnownProviders.Find(provider) is null)
                return Results.NotFound(new { error = $"\"{provider}\" is not a provider the factory knows." });

            await catalog.RefreshAsync(provider, ct);
            return Results.Ok(CatalogView(provider, catalog.Current, settings.Providers));
        });

        admin.MapPut("/settings/tools", async (
            SaveSettingsRequest<ToolSettings> request, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
        {
            if (request.Settings is not { } tools)
                return Results.BadRequest(new { error = "settings are required." });
            if (tools.Validate() is { Count: > 0 } errors)
                return Results.BadRequest(new { error = string.Join(" ", errors), errors });

            return await SaveAsync(settings, SettingsSections.Tools, tools, request.Revision, user, ct);
        });

        admin.MapPut("/settings/mcp", async (
            SaveSettingsRequest<McpSettings> request, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
        {
            if (request.Settings is not { } mcp)
                return Results.BadRequest(new { error = "settings are required." });
            if (mcp.Validate() is { Count: > 0 } errors)
                return Results.BadRequest(new { error = string.Join(" ", errors), errors });

            var saved = await SaveAsync(settings, SettingsSections.Mcp, mcp, request.Revision, user, ct);

            // A server or variable no longer listed takes its secret with it: nothing would ever read it again.
            var kept = mcp.Servers.SelectMany(s => s.SecretVariables.Select(v => SecretNames.Mcp(s.Name, v))).ToHashSet(StringComparer.Ordinal);
            foreach (var orphan in settings.Secrets.Select(s => s.Name).Where(n => n.StartsWith("mcp:", StringComparison.Ordinal) && !kept.Contains(n)))
                await settings.ClearSecretAsync(orphan, user.UserId(), ct);
            return saved;
        });

        // Starts a server once, as the form describes it and with the secrets set for it, and lists its tools (blueprint §8.1).
        admin.MapPost("/mcp/test", async (McpServerSettings? server, IMcpConnectionTester tester, CancellationToken ct) =>
        {
            if (server is null)
                return Results.BadRequest(new { error = "A server is required." });
            if (new McpSettings { Servers = [server] }.Validate() is { Count: > 0 } errors)
                return Results.BadRequest(new { error = string.Join(" ", errors), errors });

            return Results.Ok(await tester.TestAsync(server, ct));
        });

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

    /// <summary>
    /// One provider's catalog, and which of its allowed models it no longer lists. Never fetched:
    /// no times and no models.
    /// </summary>
    private static object CatalogView(string provider, ModelCatalog catalog, ProviderSettings settings)
    {
        var held = catalog.Of(provider);
        return new
        {
            Name = provider,
            held?.Fetch.AttemptedAt,
            held?.Fetch.FetchedAt,
            held?.Fetch.Error,
            Models = (held?.Models ?? []).Select(m => new
            {
                Id = m.ModelId, m.DisplayName, m.ContextLength, m.SupportsTools, m.InputPricePerMillion, m.OutputPricePerMillion,
            }),
            Retired = (settings.Entry(provider)?.Models ?? []).Where(m => catalog.IsRetired(provider, m.Id)).Select(m => m.Id),
        };
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
