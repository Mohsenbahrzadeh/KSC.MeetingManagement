using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class KscDbContext(DbContextOptions<KscDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Meeting> Meetings => Set<Meeting>();

    public DbSet<MeetingAttachment> Attachments => Set<MeetingAttachment>();

    public DbSet<Committee> Committees => Set<Committee>();

    public DbSet<MeetingNumberCounter> NumberCounters => Set<MeetingNumberCounter>();

    public DbSet<MeetingParticipant> Participants => Set<MeetingParticipant>();

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
            b.Property(m => m.Number).HasMaxLength(32);
            b.HasIndex(m => new { m.TenantId, m.Number }).IsUnique();
            b.HasIndex(m => new { m.TenantId, m.StartUtc });
            b.HasIndex(m => new { m.TenantId, m.Status });

            // Defence in depth: tenant filtering never replaces authorization checks.
            b.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MeetingAttachment>(b =>
        {
            b.ToTable("meeting_attachments");
            b.HasKey(a => a.Id);
            b.Property(a => a.TenantId).IsRequired();
            b.Property(a => a.MeetingId).IsRequired();
            b.Property(a => a.FileName).HasMaxLength(200).IsRequired();
            b.Property(a => a.ContentType).HasMaxLength(150).IsRequired();
            b.Property(a => a.SizeBytes);
            b.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
            b.Property(a => a.StorageKey).HasMaxLength(100).IsRequired();
            b.Property(a => a.UploadedBy);
            b.Property(a => a.UploadedAtUtc);
            b.HasIndex(a => new { a.TenantId, a.MeetingId });
            b.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Committee>(b =>
        {
            b.ToTable("committees");
            b.HasKey(c => c.Id);
            b.Property(c => c.TenantId).IsRequired();
            b.Property(c => c.Name).HasMaxLength(200).IsRequired();
            b.Property(c => c.Description).HasMaxLength(2000);
            b.HasIndex(c => new { c.TenantId, c.Name });
            b.HasMany(c => c.Members).WithOne().HasForeignKey(m => m.CommitteeId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(c => c.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<CommitteeMember>(b =>
        {
            b.ToTable("committee_members");
            b.HasKey(m => new { m.CommitteeId, m.UserId });
            b.Property(m => m.TenantId).IsRequired();
            b.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            b.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MeetingNumberCounter>(b =>
        {
            b.ToTable("meeting_number_counters");
            b.HasKey(c => new { c.TenantId, c.Year });
            b.Property(c => c.LastValue).IsConcurrencyToken();
        });

        modelBuilder.Entity<MeetingParticipant>(b =>
        {
            b.ToTable("meeting_participants");
            b.HasKey(p => p.Id);
            b.Property(p => p.TenantId).IsRequired();
            b.Property(p => p.MeetingId).IsRequired();
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.GuestName).HasMaxLength(200);
            b.Property(p => p.GuestEmail).HasMaxLength(320);
            b.Property(p => p.InviteTokenHash).HasMaxLength(64);
            b.HasIndex(p => new { p.TenantId, p.MeetingId });
            b.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });
    }
}
