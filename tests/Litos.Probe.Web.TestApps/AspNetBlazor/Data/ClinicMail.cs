using System.Net.Mail;
using Microsoft.AspNetCore.Identity;

namespace ClinicPortal.Data;

/// <summary>
/// The clinic's email over SMTP: Identity's messages and a "new sign-in" notice. Sent only when
/// Smtp:Host is configured (in a test run, a Mailpit service the factory starts); otherwise kept
/// in the log, as the template's no-op sender did.
/// </summary>
public sealed class ClinicMail(IConfiguration configuration, ILogger<ClinicMail> logger)
{
    public const string From = "no-reply@clinic.test";

    public async Task SendAsync(string to, string subject, string body)
    {
        if (configuration["Smtp:Host"] is not { Length: > 0 } host)
        {
            logger.LogInformation("No SMTP server configured; not sending \"{Subject}\" to {To}.", subject, to);
            return;
        }

        using var client = new SmtpClient(host, int.Parse(configuration["Smtp:Port"] ?? "25", System.Globalization.CultureInfo.InvariantCulture));
        using var message = new MailMessage(From, to, subject, body) { IsBodyHtml = body.Contains('<') };
        await client.SendMailAsync(message);
    }
}

/// <summary>Identity's emails (confirmation, password reset) through <see cref="ClinicMail"/>.</summary>
public sealed class ClinicIdentityEmailSender(ClinicMail mail) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        mail.SendAsync(email, "Confirm your email", $"Please confirm your account by <a href='{confirmationLink}'>clicking here</a>.");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        mail.SendAsync(email, "Reset your password", $"Please reset your password by <a href='{resetLink}'>clicking here</a>.");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        mail.SendAsync(email, "Reset your password", $"Please reset your password using the following code: {resetCode}");
}
