using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Host.Gateway;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// The endpoints a worker calls back on (docs/software-factory/m1-architecture.md §3). They are
/// not part of the browser's API: there is no cookie here, and each request must carry the
/// secret of the run it names, which only that run's worker was given.
/// </summary>
public static class WorkerCallbacks
{
    /// <summary>How long after recording request_decision the turn is ended. The tool's result
    /// has to get back to the model first, so the transcript shows the decision as recorded.</summary>
    public static TimeSpan DecisionStopDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    public static IEndpointRouteBuilder MapWorkerCallbacks(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/internal/runs/{runId}").AllowAnonymous();

        runs.MapPost("/ready", async (string runId, HttpRequest request, RunRegistry registry) =>
        {
            if (Authenticate(runId, request, registry) is not { } active)
                return Results.Unauthorized();

            var ready = await request.ReadFromJsonAsync<WorkerReady>(FactoryWire.Json);
            if (ready is not null)
                active.Ready.TrySetResult(ready);
            return Results.Ok();
        });

        runs.MapPost("/submissions", async (string runId, HttpRequest request, RunRegistry registry, CancellationToken ct) =>
        {
            if (Authenticate(runId, request, registry) is not { } active)
                return Results.Unauthorized();

            SubmissionRequest? submission;
            try
            {
                submission = await request.ReadFromJsonAsync<SubmissionRequest>(FactoryWire.Json, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(new SubmissionResponse(false, $"The submission could not be read: {ex.Message}"), FactoryWire.Json);
            }

            if (submission is null)
                return Results.Json(new SubmissionResponse(false, "The submission was empty."), FactoryWire.Json);

            var response = active.Accept(submission.SessionId, submission.Submission);
            if (response.Accepted && submission.Submission is DecisionSubmission)
            {
                // The run stops while it waits for the answer: end the turn once the tool has returned.
                _ = Task.Delay(DecisionStopDelay, CancellationToken.None).ContinueWith(_ => active.CancelTurn(), TaskScheduler.Default);
            }

            return Results.Json(response, FactoryWire.Json);
        });

        runs.MapPost("/gateway", async (string runId, HttpContext context, RunRegistry registry, ModelGateway gateway) =>
        {
            if (Authenticate(runId, context.Request, registry) is not { } active)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            GatewayRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<GatewayRequest>(FactoryWire.Json, context.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.RequestKey))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.ContentType = "application/x-ndjson";
            try
            {
                await gateway.HandleAsync(active, request, async evt =>
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(evt, FactoryWire.Json) + "\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // The worker's turn was cancelled; the gateway has already accounted for the call.
            }
        });

        return app;
    }

    private static ActiveRun? Authenticate(string runId, HttpRequest request, RunRegistry registry) =>
        Guid.TryParseExact(runId, "N", out var id)
        && registry.Find(id) is { } active
        && active.HasSecret(request.Headers[FactoryWire.SecretHeader].ToString())
            ? active
            : null;
}
