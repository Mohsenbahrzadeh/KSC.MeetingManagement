using KSC.MeetingManagement.Application.Meetings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using KSC.MeetingManagement.Application.Agenda;
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
