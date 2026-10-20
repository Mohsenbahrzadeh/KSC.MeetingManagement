using KSC.MeetingManagement.Application.Meetings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        return services;
    }
}
