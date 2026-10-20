using System.Globalization;
using KSC.MeetingManagement.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KSC.MeetingManagement.Infrastructure.Notifications;

/// <summary>Whether queued notifications are active; the background worker only runs when they are.</summary>
public sealed record NotificationRuntime(bool Enabled, TimeSpan PollInterval, TimeSpan ReminderInterval);

/// <summary>Plain date text used until the host supplies a calendar-aware formatter.</summary>
public sealed class IsoNotificationDateFormatter : INotificationDateFormatter
{
    public string Format(DateTimeOffset instantUtc, string? timeZoneId)
    {
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found))
        {
            zone = found;
        }

        return TimeZoneInfo.ConvertTime(instantUtc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}

public static class NotificationRegistration
{
    /// <summary>
    /// Registers the outbox, delivery and reminders. Everything is switched on by Notifications:PayloadKey (base64, 32 bytes), which protects
    /// queued text at rest. Without it nothing is queued or sent. Email is used when Notifications:Smtp:Host is set; otherwise messages
    /// only go to the log (kind and id, never text).
    /// </summary>
    public static IServiceCollection AddKscNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var protector = AesGcmSecretProtector.TryCreate(configuration["Notifications:PayloadKey"]);
        services.TryAddSingleton(new NotificationOptions
        {
            MaxAttempts = Int(configuration["Notifications:MaxAttempts"], 6),
            BatchSize = Int(configuration["Notifications:BatchSize"], 20),
            BaseDelay = TimeSpan.FromSeconds(Int(configuration["Notifications:RetryBaseSeconds"], 30)),
        });

        if (protector is null)
        {
            services.TryAddScoped<Application.Notifications.IOutboxRepository, DisabledOutboxRepository>();
            services.TryAddSingleton<ISecretProtector, DisabledSecretProtector>();
        }
        else
        {
            services.TryAddScoped<Application.Notifications.IOutboxRepository, EfOutboxRepository>();
            services.TryAddSingleton<ISecretProtector>(protector);
        }

        services.TryAddSingleton(new NotificationRuntime(
            protector is not null,
            TimeSpan.FromSeconds(Int(configuration["Notifications:PollSeconds"], 15)),
            TimeSpan.FromSeconds(Int(configuration["Notifications:ReminderSeconds"], 60))));

        var smtp = new SmtpOptions
        {
            Host = configuration["Notifications:Smtp:Host"],
            Port = Int(configuration["Notifications:Smtp:Port"], 587),
            User = configuration["Notifications:Smtp:User"],
            Password = configuration["Notifications:Smtp:Password"],
            From = configuration["Notifications:Smtp:From"],
            EnableSsl = !string.Equals(configuration["Notifications:Smtp:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase),
        };
        if (!string.IsNullOrWhiteSpace(smtp.Host) && !string.IsNullOrWhiteSpace(smtp.From))
        {
            services.TryAddSingleton<INotificationChannel>(new SmtpNotificationChannel(smtp));
        }
        else
        {
            services.TryAddSingleton<INotificationChannel, LoggingNotificationChannel>();
        }

        services.TryAddSingleton<IContactDirectory, NullContactDirectory>();
        services.TryAddSingleton<INotificationDateFormatter, IsoNotificationDateFormatter>();
        services.TryAddScoped<IReminderSource, EfReminderSource>();
        services.TryAddScoped(sp => new NotificationService(
            sp.GetRequiredService<Application.Notifications.IOutboxRepository>(),
            sp.GetRequiredService<Application.Participants.IParticipantRepository>(),
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<INotificationDateFormatter>(),
            sp.GetRequiredService<Application.Tenants.ITenantContext>(),
            sp.GetRequiredService<TimeProvider>())
        {
            PublicBaseUrl = configuration["Notifications:PublicBaseUrl"],
            Enabled = protector is not null,
        });
        services.TryAddScoped<OutboxProcessor>();
        services.TryAddScoped<ReminderService>();
        return services;
    }

    private static int Int(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;
}
