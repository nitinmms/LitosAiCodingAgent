---
name: testability-dotnet
version: 1
description: Use in a Make-testable task on an ASP.NET Core app (Blazor Server, MVC, Razor Pages): the Testing environment, test users from variables, /__test endpoints, the readiness marker, and the traps check_testability reports.
defaultFor: testability
---

Applies when the detected stack is `aspnet-blazor-server` or another ASP.NET Core app. The brief comes first; this is how to meet it on .NET.

## The Testing environment
- The contract runs the app with `ASPNETCORE_ENVIRONMENT=Testing`. Gate everything test-only on `builder.Environment.IsEnvironment("Testing")`.
- In Testing, call `builder.WebHost.UseStaticWebAssets()`: only Development loads a build's static web assets, so without it `blazor.web.js` is missing and no Blazor circuit ever starts.
- Skip `app.UseHttpsRedirection()` in Testing: the factory serves plain `http://localhost`, and a redirect to https breaks sign-in. Secure-only cookies, likewise.
- The serve command has `--no-launch-profile`, or launchSettings.json overrides the port the factory passes in ASPNETCORE_URLS.

## Test users
- Seed one user per role at start-up in Testing only, from variables the contract passes (TEST_ADMIN_USER, TEST_ADMIN_PASS, ...), with `UserManager<T>` and `RoleManager<T>`. Confirm the email when sign-in needs a confirmed account. If the user exists, reset the password to the variable's value.
- Never put a password in code or in the contract: it is `{secret:TEST_ADMIN_PASS}` there, set by a person.

## Data and reset
- Prefer a Postgres service container (or the database the app uses) with `{generated}` passwords. Create the schema at start-up (migrations or `Database.EnsureCreated()`).
- A `POST /__test/reset` endpoint (`MapGroup("/__test")`, Testing only) that deletes and re-seeds the domain data is the fastest reset.
- Npgsql refuses a local DateTime for a timestamptz column: map wall-clock times to `timestamp without time zone`, or store UTC.

## Pages
- Blazor Server needs the readiness marker `[data-testid=app-interactive]`, rendered only once the component runs interactively (`RendererInfo.IsInteractive`), in the layout with its own `@rendermode InteractiveServer`, so every page has it.
- Every input needs a `<label for>`; every icon-only button an `aria-label`. The Gate's lint fails a control with neither a name nor a `data-testid`.
- The Identity template's `PasskeySubmit.razor.js` logs `console.error` when passkey autofill finds no passkey, as in any headless browser: log it as a warning instead.
- Protect pages with `[Authorize(Roles = "...")]`; the Gate checks a signed-out visit redirects to the login page and a lower role is refused.
