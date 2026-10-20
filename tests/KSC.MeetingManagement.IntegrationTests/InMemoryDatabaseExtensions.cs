using KSC.MeetingManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KSC.MeetingManagement.IntegrationTests;

internal static class InMemoryDatabaseExtensions
{
    /// <summary>Swaps the PostgreSQL registration of KscDbContext for an isolated in-memory database.</summary>
    public static IServiceCollection UseInMemoryKscDatabase(this IServiceCollection services, string name)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<KscDbContext>)
                || d.ServiceType == typeof(DbContextOptions)
                || (d.ServiceType.IsGenericType
                    && d.ServiceType.GenericTypeArguments.Length == 1
                    && d.ServiceType.GenericTypeArguments[0] == typeof(KscDbContext)))
            .ToList();
        foreach (var d in toRemove)
        {
            services.Remove(d);
        }

        return services.AddDbContext<KscDbContext>(o => o.UseInMemoryDatabase(name));
    }
}
