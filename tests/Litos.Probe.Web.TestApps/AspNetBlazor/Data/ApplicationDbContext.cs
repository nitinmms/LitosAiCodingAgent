using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ClinicPortal.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();
}

public class Appointment
{
    public int Id { get; set; }
    public string PatientEmail { get; set; } = "";
    public string Clinician { get; set; } = "";
    public DateTime StartsAt { get; set; }
    public string Reason { get; set; } = "";
}
