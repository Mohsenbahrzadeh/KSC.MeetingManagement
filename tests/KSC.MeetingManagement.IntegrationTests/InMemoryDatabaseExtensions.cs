using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Application.Meetings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace KSC.MeetingManagement.IntegrationTests;

internal static class InMemoryDatabaseExtensions
{
    /// <summary>Env var holding a PostgreSQL connection string (any database); when set, tests run against real PostgreSQL.</summary>
    private const string PostgresEnvVar = "KSC_TEST_POSTGRES";

    /// <summary>
    /// Swaps the PostgreSQL registration of KscDbContext for an isolated in-memory database,
    /// or, when KSC_TEST_POSTGRES is set, for a fresh PostgreSQL database migrated from the real migrations.
    /// </summary>
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

        services.RemoveAll<IMeetingContentIndex>();
        services.AddSingleton<IMeetingContentIndex, InMemoryMeetingContentIndex>();

        var pg = Environment.GetEnvironmentVariable(PostgresEnvVar);
        if (string.IsNullOrWhiteSpace(pg))
        {
            return services.AddDbContext<KscDbContext>(o => o.UseInMemoryDatabase(name));
        }

        var builder = new NpgsqlConnectionStringBuilder(pg) { Database = "t_" + name.Replace("-", "", StringComparison.Ordinal) };
        var connectionString = builder.ConnectionString;
        services.AddDbContext<KscDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton<IHostedService>(sp => new MigrateOnStart(sp));
        return services;
    }

    private sealed class MigrateOnStart(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<KscDbContext>().Database.MigrateAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
