---
name: testability-dotnet
version: 2
description: Use in a Make-testable task on an ASP.NET Core app (Blazor Server, MVC, Razor Pages): the Testing environment, test users from variables, /__test endpoints, the readiness marker, and the traps check_testability reports.
defaultFor: testability
---

Applies when the detected stack is `aspnet-blazor-server` or another ASP.NET Core app. The brief comes first; this is how to meet it on .NET.

## The Testing environment
- The contract runs the app with `ASPNETCORE_ENVIRONMENT=Testing`. Gate everything test-only on `builder.Environment.IsEnvironment("Testing")`.
- In Testing, call `builder.WebHost.UseStaticWebAssets()`: only Development loads a build's static web assets, so without it `blazor.web.js` is missing and no Blazor circuit ever starts.
- Skip `app.UseHttpsRedirection()` and secure-only cookies in Testing: the factory serves plain `http://localhost`, and https breaks sign-in.
- Serve with `--no-launch-profile`, or launchSettings.json overrides the port the factory passes in ASPNETCORE_URLS.

## Test users
- Seed one user per role at start-up in Testing only, from variables the contract passes (TEST_ADMIN_USER, TEST_ADMIN_PASS, ...), with `UserManager<T>` and `RoleManager<T>`. Confirm the email when sign-in needs a confirmed account. If the user exists, reset the password to the variable's value.
- Never put a password in code or the contract: there it is `{secret:TEST_ADMIN_PASS}`.

## Unit tests
- No test project yet: create `Tests/<App>.Tests.csproj` (xUnit) referencing the app, and a root `<App>.slnx` listing both, so `dotnet test` at the root finds it.
- An app project at the root compiles every `.cs` below it, tests too: add `<Compile Remove="Tests/**" />` (and the same for Content and None).
- Tests sharing one database (an in-memory SQLite connection) must not run in parallel: one `[Collection("Database")]`, or a database per test. Otherwise they fail now and then ("Cannot create a DbSet").

## Data and reset
- Prefer a service container for the app's database, with `{generated}` passwords. Create the schema at start-up (migrations or `Database.EnsureCreated()`).
- A `POST /__test/reset` endpoint (`MapGroup("/__test")`, Testing only) that deletes and re-seeds the domain data is the fastest reset.
- Seed domain data too, not just users: a few records of each main entity, owned by the test users, in each state the pages tell apart (an upcoming and a past appointment), so pages show real content.
- Npgsql refuses a local DateTime for a timestamptz column: map wall-clock times to `timestamp without time zone`, or store UTC.

## Pages
- Blazor Server needs the readiness marker `[data-testid=app-interactive]`, rendered only once the component runs interactively (`RendererInfo.IsInteractive`), in the layout with its own `@rendermode InteractiveServer`, so every page has it.
- Every input needs a `<label for>`; every icon-only button an `aria-label`. The Gate's lint fails a control with neither a name nor a `data-testid`.
- Never silence a Gate signal (`console.error` made a warning): fix its cause. The profile ignores the passkey script's headless error.
- Protect pages with `[Authorize(Roles = "...")]`: the Gate checks signed-out visits redirect to login and lower roles are refused.
