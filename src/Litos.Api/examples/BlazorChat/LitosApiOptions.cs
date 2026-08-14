namespace Litos.Examples.BlazorChat;

/// <summary>Bound from the "LitosApi" config section — appsettings.json, environment variables (LitosApi__BaseUrl), or user-secrets.</summary>
public sealed class LitosApiOptions
{
    public const string SectionName = "LitosApi";

    public string BaseUrl { get; set; } = "http://localhost:8080";
}
