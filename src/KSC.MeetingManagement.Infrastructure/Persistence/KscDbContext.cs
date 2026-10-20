using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Domain.Audit;
using KSC.MeetingManagement.Domain.Notifications;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.EntityFrameworkCore;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public sealed class KscDbContext(DbContextOptions<KscDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Meeting> Meetings => Set<Meeting>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<ActionItem> ActionItems => Set<ActionItem>();

    public DbSet<Resolution> Resolutions => Set<Resolution>();

    public DbSet<Minutes> Minutes => Set<Minutes>();

    public DbSet<AgendaItem> AgendaItems => Set<AgendaItem>();

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
            b.Property(m => m.AgendaRevision).IsConcurrencyToken();
            b.HasIndex(m => new { m.TenantId, m.CommitteeId });
            b.Property(m => m.Number).HasMaxLength(32);
            b.HasIndex(m => new { m.TenantId, m.Number }).IsUnique();
            b.HasIndex(m => new { m.TenantId, m.StartUtc });
            b.HasIndex(m => new { m.TenantId, m.Status });

            // Defence in depth: tenant filtering never replaces authorization checks.
            b.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.Property(m => m.TenantId).IsRequired();
            b.Property(m => m.Kind).HasConversion<string>().HasMaxLength(30);
            b.Property(m => m.RecipientType).HasConversion<string>().HasMaxLength(20);
            b.Property(m => m.RecipientUserId);
            b.Property(m => m.RecipientAddress).HasMaxLength(320);
            b.Property(m => m.RecipientName).HasMaxLength(200);
            b.Property(m => m.Subject).HasMaxLength(200).IsRequired();
            b.Property(m => m.ProtectedBody).IsRequired();
            b.Property(m => m.DedupeKey).HasMaxLength(200).IsRequired();
            b.Property(m => m.CreatedAtUtc);
            b.Property(m => m.NextAttemptAtUtc);
            b.Property(m => m.Attempts);
            b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(m => m.LockedUntilUtc);
            b.Property(m => m.SentAtUtc);
            b.Property(m => m.LastError).HasMaxLength(OutboxMessage.MaxErrorLength);
            b.Property(m => m.ClaimVersion).IsConcurrencyToken();
            b.HasIndex(m => new { m.TenantId, m.DedupeKey }).IsUnique();
            b.HasIndex(m => new { m.Status, m.NextAttemptAtUtc });
            b.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("audit_events");
            b.HasKey(e => e.Id);
            b.Property(e => e.TenantId).IsRequired();
            b.Property(e => e.OccurredAtUtc);
            b.Property(e => e.ActorType).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.ActorId);
            b.Property(e => e.Action).HasMaxLength(AuditEvent.MaxActionLength).IsRequired();
            b.Property(e => e.ResourceType).HasMaxLength(AuditEvent.MaxResourceTypeLength).IsRequired();
            b.Property(e => e.ResourceId);
            b.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.CorrelationId).HasMaxLength(64);
            b.Property(e => e.Detail).HasMaxLength(AuditEvent.MaxDetailLength);
            b.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
            b.HasIndex(e => new { e.TenantId, e.ResourceId });
            b.HasIndex(e => new { e.TenantId, e.ActorId });
            b.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ActionItem>(b =>
        {
            b.ToTable("action_items");
            b.HasKey(a => a.Id);
            b.Property(a => a.TenantId).IsRequired();
            b.Property(a => a.MeetingId).IsRequired();
            b.Property(a => a.ResolutionId);
            b.Property(a => a.Title).HasMaxLength(ActionItem.MaxTitleLength).IsRequired();
            b.Property(a => a.Description).HasMaxLength(ActionItem.MaxDescriptionLength);
            b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(a => a.Note).HasMaxLength(ActionItem.MaxNoteLength);
            b.Property(a => a.LockVersion).IsConcurrencyToken();
            b.Ignore(a => a.IsActive);
            b.HasIndex(a => new { a.TenantId, a.MeetingId });
            b.HasIndex(a => new { a.TenantId, a.OwnerUserId, a.Status });
            b.HasIndex(a => new { a.TenantId, a.Status, a.DueDate });
            b.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Resolution>(b =>
        {
            b.ToTable("resolutions");
            b.HasKey(r => r.Id);
            b.Property(r => r.TenantId).IsRequired();
            b.Property(r => r.MeetingId).IsRequired();
            b.Property(r => r.Number);
            b.Property(r => r.Text).HasMaxLength(Resolution.MaxTextLength).IsRequired();
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.RevocationReason).HasMaxLength(Resolution.MaxReasonLength);
            b.Property(r => r.LockVersion).IsConcurrencyToken();
            b.HasIndex(r => new { r.TenantId, r.MeetingId, r.Number }).IsUnique();
            b.HasIndex(r => r.AgendaItemId);
            b.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Minutes>(b =>
        {
            b.ToTable("minutes");
            b.HasKey(m => m.Id);
            b.Property(m => m.TenantId).IsRequired();
            b.Property(m => m.MeetingId).IsRequired();
            b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(m => m.LockVersion).IsConcurrencyToken();
            b.Ignore(m => m.CurrentVersion);
            b.HasIndex(m => new { m.TenantId, m.MeetingId }).IsUnique();
            b.HasMany(m => m.Versions).WithOne().HasForeignKey(v => v.MinutesId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(m => m.Transitions).WithOne().HasForeignKey(t => t.MinutesId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(m => m.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(m => m.Transitions).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MinutesVersion>(b =>
        {
            b.ToTable("minutes_versions");
            b.HasKey(v => new { v.MinutesId, v.Number });
            b.Property(v => v.TenantId).IsRequired();
            b.Property(v => v.Content).IsRequired();
            b.Property(v => v.AuthorUserId);
            b.Property(v => v.CreatedAtUtc);
            b.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<MinutesTransition>(b =>
        {
            b.ToTable("minutes_transitions");
            b.HasKey(t => t.Id);

            // Ids are created by the application; without this EF treats a child added to a loaded parent as existing and tries to UPDATE it.
            b.Property(t => t.Id).ValueGeneratedNever();
            b.Property(t => t.TenantId).IsRequired();
            b.Property(t => t.From).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.To).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.Comment).HasMaxLength(Domain.Meetings.Minutes.MaxCommentLength);
            b.Property(t => t.VersionNumber);
            b.Property(t => t.ActorUserId);
            b.Property(t => t.AtUtc);
            b.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AgendaItem>(b =>
        {
            b.ToTable("agenda_items");
            b.HasKey(i => i.Id);
            b.Property(i => i.TenantId).IsRequired();
            b.Property(i => i.MeetingId).IsRequired();
            b.Property(i => i.Order);
            b.Property(i => i.Title).HasMaxLength(AgendaItem.MaxTitleLength).IsRequired();
            b.Property(i => i.Description).HasMaxLength(AgendaItem.MaxDescriptionLength);
            b.HasIndex(i => new { i.TenantId, i.MeetingId, i.Order });
            b.HasQueryFilter(i => i.TenantId == CurrentTenantId);
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
