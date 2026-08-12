namespace Litos.Examples.BlazorChat;

/// <summary>Bound from the "LitosApi" config section — appsettings.json, environment variables (LitosApi__AdminToken), or user-secrets. AdminToken is left empty in source control on purpose; see README for how to supply it.</summary>
public sealed class LitosApiOptions
{
    public const string SectionName = "LitosApi";

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public string AdminToken { get; set; } = "";
}
