namespace Litos.Probe.Web.Core.Reports;

/// <summary>Why a check failed, as the engine observed it.</summary>
public enum FailureCause
{
    BuildFailed,
    DockerUnavailable,
    ServiceNotReady,
    ProcessExited,
    NotReady,
    WrongPort,
    HttpsRedirect,
    RootServerError,
    ResetFailed,
    SecretMissing,
    LoginFieldUnlabelled,
    SignInNotReached,
    PageServerError,
    PageSignal,
    ServerLogError,
    MarkerMissing,
    RouteNotFound,
    SignedOutDuringVisit,
    NotRedirectedToLogin,
    DeniedRouteOpened,
    TestEndpointInProduction,
    ProductionStartFailed,
    UnnamedElement,
    LocatorNotFound,
    Timeout,
}

/// <summary>
/// Probe's classifier (README §7, testability-architecture.md §7.4), as one table. The factory's
/// action follows from the class alone: repair, a person, or Blocked.
/// </summary>
public static class Classifier
{
    public static Classification Classify(FailureCause cause) => cause switch
    {
        // The app could not be brought up or reset: nothing to repair against, so Blocked.
        FailureCause.BuildFailed or FailureCause.DockerUnavailable or FailureCause.ServiceNotReady or FailureCause.ProcessExited or FailureCause.NotReady or FailureCause.ResetFailed
            or FailureCause.Timeout => Classification.EnvironmentFailure,

        // The credentials, not the code.
        FailureCause.SecretMissing or FailureCause.SignInNotReached => Classification.AuthenticationFailure,

        // The contract or a scenario no longer matches the app: a person decides which is right.
        FailureCause.RouteNotFound or FailureCause.LocatorNotFound => Classification.TestDefect,

        // Everything else is the app behaving wrongly, including being untestable: an app that
        // ignores its port, forces HTTPS, or drops a field label broke the contract it agreed to.
        _ => Classification.ApplicationDefect,
    };

    public static CheckResult Fail(string id, CheckKind kind, FailureCause cause, string message) =>
        CheckResult.Fail(id, kind, Classify(cause), message);
}
