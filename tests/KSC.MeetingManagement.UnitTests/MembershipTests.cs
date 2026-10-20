using KSC.MeetingManagement.Domain.Identity;

namespace KSC.MeetingManagement.UnitTests;

public sealed class MembershipTests
{
    [Fact]
    public void Active_member_has_assigned_roles()
    {
        var m = new Membership(Guid.NewGuid(), Guid.NewGuid(), [Roles.Secretary]);

        Assert.True(m.HasRole(Roles.Secretary));
        Assert.False(m.HasRole(Roles.TenantOwner));
    }

    [Fact]
    public void Disabled_member_has_no_effective_roles()
    {
        var m = new Membership(Guid.NewGuid(), Guid.NewGuid(), [Roles.TenantOwner]);

        m.Disable();

        Assert.False(m.HasRole(Roles.TenantOwner));
    }

    [Fact]
    public void Invited_member_is_not_active() =>
        Assert.False(new Membership(Guid.NewGuid(), Guid.NewGuid(), [Roles.Participant], MembershipStatus.Invited).HasRole(Roles.Participant));

    [Fact]
    public void Empty_ids_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new Membership(Guid.Empty, Guid.NewGuid(), []));
}
