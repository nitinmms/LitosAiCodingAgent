using Microsoft.Extensions.Logging;

namespace Litos.Api.Logs;

public sealed record LogEntry(
    LogLevel Level,
    DateTimeOffset Timestamp,
    string Category,
    string Message,
    Exception? Exception);
