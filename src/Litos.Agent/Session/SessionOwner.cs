namespace Litos.Agent.Session;

public readonly record struct SessionOwner(string Value)
{
    public static SessionOwner Local { get; } = new("local");

    public static SessionOwner Telegram { get; } = new("telegram");
}
