using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

public sealed class ContactDirectoryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Addresses_come_from_active_accounts_of_active_members_only()
    {
        var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.UseInMemoryKscDatabase(Guid.NewGuid().ToString())));
        var tenant = new Tenant(Guid.NewGuid(), "t", "T", "UTC");
        var other = new Tenant(Guid.NewGuid(), "o", "O", "UTC");
        var active = Guid.NewGuid();
        var disabledMember = Guid.NewGuid();
        var inactiveUser = Guid.NewGuid();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KscDbContext>();
        db.Tenants.AddRange(tenant, other);
        db.Users.AddRange(
            new User(active, "A@example.com", "Ali", "h", DateTimeOffset.UtcNow),
            new User(disabledMember, "b@example.com", "B", "h", DateTimeOffset.UtcNow),
            new User(inactiveUser, "c@example.com", "C", "h", DateTimeOffset.UtcNow));
        db.Users.Local.Single(u => u.Id == inactiveUser).Deactivate();
        db.Memberships.AddRange(
            MembershipRow.From(new Membership(tenant.Id, active, [Roles.Participant])),
            MembershipRow.From(new Membership(tenant.Id, disabledMember, [Roles.Participant], MembershipStatus.Disabled)),
            MembershipRow.From(new Membership(tenant.Id, inactiveUser, [Roles.Participant])));
        await db.SaveChangesAsync();

        var directory = scope.ServiceProvider.GetRequiredService<IContactDirectory>();

        var contact = await directory.FindAsync(tenant.Id, active, CancellationToken.None);
        Assert.Equal("a@example.com", contact?.Address);
        Assert.Equal("Ali", contact?.DisplayName);
        Assert.Null(await directory.FindAsync(tenant.Id, disabledMember, CancellationToken.None));
        Assert.Null(await directory.FindAsync(tenant.Id, inactiveUser, CancellationToken.None));
        Assert.Null(await directory.FindAsync(other.Id, active, CancellationToken.None));
    }
}
