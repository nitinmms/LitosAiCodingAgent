using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ClinicPortal.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // An appointment is a clinic's wall-clock time, not an instant: on Postgres that is
        // "timestamp without time zone" (Npgsql refuses a local DateTime for timestamptz).
        if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            builder.Entity<Appointment>().Property(a => a.StartsAt).HasColumnType("timestamp without time zone");
    }
}

public class Appointment
{
    public int Id { get; set; }
    public string PatientEmail { get; set; } = "";
    public string Clinician { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public string Reason { get; set; } = "";
}
