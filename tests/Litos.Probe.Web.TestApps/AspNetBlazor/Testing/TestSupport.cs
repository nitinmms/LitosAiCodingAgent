using ClinicPortal.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicPortal.Testing;

/// <summary>The clock pages show. <c>POST /__test/clock</c> sets it in Testing; otherwise it is real time.</summary>
public sealed class ClinicClock
{
    public DateTime? Fixed { get; set; }
    public DateTime Now => Fixed ?? DateTime.Now;
}

/// <summary>
/// What makes the app testable, registered only when ASPNETCORE_ENVIRONMENT is Testing: one test
/// user per role from the TEST_* variables (the Gate's role secrets reach the app as these), and
/// the /__test/* endpoints. Gate step 7 checks the endpoints are gone in Production.
/// </summary>
public static class TestSupport
{
    public static readonly string[] Roles = ["admin", "staff", "patient"];

    public static async Task SeedUsersAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        foreach (var role in Roles)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

            var email = configuration[$"TEST_{role.ToUpperInvariant()}_USER"];
            var password = configuration[$"TEST_{role.ToUpperInvariant()}_PASS"];
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                continue;

            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
                Check(await users.CreateAsync(user, password));
            }
            else
            {
                // The secret may have changed since the last run: the variable always wins.
                Check(await users.RemovePasswordAsync(user));
                Check(await users.AddPasswordAsync(user, password));
            }

            if (!await users.IsInRoleAsync(user, role))
                Check(await users.AddToRoleAsync(user, role));
        }
    }

    public static async Task ResetAppointmentsAsync(ApplicationDbContext db, IConfiguration configuration)
    {
        await db.Appointments.ExecuteDeleteAsync();
        var patient = configuration["TEST_PATIENT_USER"] ?? "patient@clinic.test";
        var today = DateTime.Today;
        db.Appointments.AddRange(
            new Appointment { PatientEmail = patient, Clinician = "Dr Rao", StartsAt = today.AddDays(1).AddHours(9), Reason = "Check-up" },
            new Appointment { PatientEmail = patient, Clinician = "Dr Okafor", StartsAt = today.AddDays(7).AddHours(14), Reason = "Blood test results" },
            new Appointment { PatientEmail = "someone.else@clinic.test", Clinician = "Dr Rao", StartsAt = today.AddDays(2).AddHours(11), Reason = "Physiotherapy" });
        await db.SaveChangesAsync();
    }

    public static void MapTestEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/__test");
        group.MapPost("/reset", async (ApplicationDbContext db, IConfiguration configuration) =>
        {
            await ResetAppointmentsAsync(db, configuration);
            return Results.Ok();
        });
        group.MapPost("/clock", (ClinicClock clock, ClockRequest? request) =>
        {
            clock.Fixed = request?.Now;
            return Results.Ok(new { now = clock.Now });
        });
    }

    public sealed record ClockRequest(DateTime? Now);

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Seeding a test user failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
