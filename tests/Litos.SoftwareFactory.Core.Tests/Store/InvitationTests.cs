using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Tests.Store;

/// <summary>When an invitation's link still creates an account (m2-architecture.md §4).</summary>
public class InvitationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static Invitation Fresh() => new() { UserName = "erin", TokenHash = new string('a', 64), CreatedAt = T0, ExpiresAt = T0.AddDays(7) };

    [Fact]
    public void AFreshInvitation_IsUsableUntilItExpires()
    {
        var invitation = Fresh();

        Assert.True(invitation.IsUsable(T0));
        Assert.True(invitation.IsUsable(T0.AddDays(7).AddTicks(-1)));
        Assert.False(invitation.IsUsable(T0.AddDays(7)));
    }

    [Fact]
    public void AnAcceptedInvitation_IsNotUsable()
    {
        var invitation = Fresh();
        invitation.AcceptedAt = T0.AddHours(1);

        Assert.False(invitation.IsUsable(T0.AddHours(2)));
    }

    [Fact]
    public void ARevokedInvitation_IsNotUsable()
    {
        var invitation = Fresh();
        invitation.RevokedAt = T0.AddHours(1);

        Assert.False(invitation.IsUsable(T0.AddHours(2)));
    }

    [Fact]
    public void UnusableReason_SaysWhyALinkStoppedWorking_AndIsNullWhileItWorks()
    {
        var invitation = Fresh();
        Assert.Null(invitation.UnusableReason(T0));
        Assert.Contains("expired", invitation.UnusableReason(T0.AddDays(8)));

        invitation.RevokedAt = T0.AddHours(1);
        Assert.Contains("revoked", invitation.UnusableReason(T0.AddHours(2)));

        // Used is the most specific answer, even after it has also expired.
        invitation.AcceptedAt = T0.AddHours(1);
        Assert.Equal("This invitation has already been used.", invitation.UnusableReason(T0.AddDays(30)));
    }

    [Fact]
    public void ANewInvitation_IsForAMember_JoiningNoProject()
    {
        var invitation = Fresh();

        Assert.Equal(AccountRole.Member, invitation.Role);
        Assert.Equal("[]", invitation.ProjectIdsJson);
    }
}
