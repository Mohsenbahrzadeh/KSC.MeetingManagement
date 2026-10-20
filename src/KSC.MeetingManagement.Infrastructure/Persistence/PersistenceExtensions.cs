using KSC.MeetingManagement.Application.Media;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Application.Reports;
using KSC.MeetingManagement.Infrastructure.Media;
using KSC.MeetingManagement.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Agenda;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Application.Attachments;
using KSC.MeetingManagement.Application.Committees;
using KSC.MeetingManagement.Application.Minutes;
using KSC.MeetingManagement.Application.Resolutions;
using KSC.MeetingManagement.Infrastructure.Storage;
using KSC.MeetingManagement.Application.Participants;

namespace KSC.MeetingManagement.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers room token issuing. Settings come from configuration section "Media" (ServerUrl, ApiKey, ApiSecret);
    /// the secret must come from environment variables or secret storage. Without complete settings joining returns 503.
    /// </summary>
    public static IServiceCollection AddKscMedia(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new MediaOptions
        {
            ServerUrl = configuration["Media:ServerUrl"],
            ApiKey = configuration["Media:ApiKey"],
            ApiSecret = configuration["Media:ApiSecret"],
            TokenLifetimeMinutes = IntOr(configuration["Media:TokenLifetimeMinutes"], 10),
            JoinOpensMinutesBeforeStart = IntOr(configuration["Media:JoinOpensMinutesBeforeStart"], 15),
            JoinClosesMinutesAfterEnd = IntOr(configuration["Media:JoinClosesMinutesAfterEnd"], 120),
            LobbyForGuests = !string.Equals(configuration["Media:LobbyForGuests"], "false", StringComparison.OrdinalIgnoreCase),
            LobbyForMembers = string.Equals(configuration["Media:LobbyForMembers"], "true", StringComparison.OrdinalIgnoreCase),
            ApiUrl = configuration["Media:ApiUrl"],
        };

        services.TryAddSingleton(options);
        services.TryAddSingleton<IMediaTokenService>(sp => new LiveKitTokenService(sp.GetRequiredService<MediaOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IRoomEntryRepository, EfRoomEntryRepository>();
        services.AddScoped<MediaAccessService>();
        services.AddScoped<RoomControlService>();
        services.TryAddSingleton<IMediaRoomControl>(sp => sp.GetRequiredService<MediaOptions>().IsComplete
            ? new LiveKitRoomControl(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, sp.GetRequiredService<MediaOptions>(), sp.GetRequiredService<TimeProvider>())
            : new NoMediaRoomControl());
        return services;
    }

    private static int IntOr(string? value, int fallback) => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    /// <summary>
    /// Registers EF Core on PostgreSQL. The connection string (ConnectionStrings:Default) must come from
    /// environment variables or user secrets, never from committed files with credentials.
    /// </summary>
    public static IServiceCollection AddKscPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Default");
        services.AddDbContext<KscDbContext>(o => o.UseNpgsql(connectionString));

        services.AddScoped<IMeetingRepository, EfMeetingRepository>();
        services.AddScoped<MeetingService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IParticipantRepository, EfParticipantRepository>();
        services.AddScoped<ParticipantService>();
        services.AddScoped<IAttachmentRepository, EfAttachmentRepository>();
        services.TryAddSingleton(new AttachmentOptions
        {
            MaxBytes = long.TryParse(configuration["Attachments:MaxBytes"], out var maxBytes) && maxBytes > 0
                ? maxBytes
                : AttachmentOptions.DefaultMaxBytes,
        });
        services.TryAddSingleton<IFileStore>(_ => new LocalFileStore(
            configuration["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "files")));
        services.AddScoped<AttachmentService>();
        services.AddKscMedia(configuration);
        services.AddKscNotifications(configuration);
        services.AddScoped<EfAuditLog>();
        services.AddScoped<IAuditLog>(sp => sp.GetRequiredService<EfAuditLog>());
        services.AddScoped<IAuditQuery>(sp => sp.GetRequiredService<EfAuditLog>());
        services.AddScoped<AuditQueryService>();
        services.AddScoped<IReportRepository, EfReportRepository>();
        services.AddScoped<ReportService>();
        services.AddScoped<IActionItemRepository, EfActionItemRepository>();
        services.AddScoped<ActionItemService>();
        services.AddScoped<IResolutionRepository, EfResolutionRepository>();
        services.AddScoped<ResolutionService>();
        services.AddScoped<IMinutesRepository, EfMinutesRepository>();
        services.AddScoped<MinutesService>();
        services.AddScoped<IAgendaRepository, EfAgendaRepository>();
        services.AddScoped<AgendaService>();
        services.AddScoped<ICommitteeRepository, EfCommitteeRepository>();
        services.AddScoped<CommitteeService>();
        return services;
    }
}
