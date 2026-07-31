namespace Litos.Agent.Streaming;

public enum SteeringMode { Steer, FollowUp }

public sealed record SteeringMessage(string Text, SteeringMode Mode);
