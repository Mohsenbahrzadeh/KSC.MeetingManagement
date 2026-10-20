using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class KscDbContext(DbContextOptions<KscDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Meeting> Meetings => Set<Meeting>();

    // Evaluated per context instance. Guid.Empty (no tenant resolved) matches nothing.
    private Guid CurrentTenantId => tenant.Tenant?.Id ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Meeting>(b =>
        {
            b.ToTable("meetings");
            b.HasKey(m => m.Id);
            b.Property(m => m.TenantId).IsRequired();
            b.Property(m => m.OrganizerId).IsRequired();
            b.Property(m => m.Title).HasMaxLength(300).IsRequired();
            b.Property(m => m.Description).HasMaxLength(4000);
            b.Property(m => m.CancellationReason).HasMaxLength(1000);
            b.Property(m => m.Mode).HasConversion<string>().HasMaxLength(20);
            b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(m => m.Version).IsConcurrencyToken();
            b.HasIndex(m => new { m.TenantId, m.StartUtc });
            b.HasIndex(m => new { m.TenantId, m.Status });

            // Defence in depth: tenant filtering never replaces authorization checks.
            b.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });
    }
}
