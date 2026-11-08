using KSC.MeetingManagement.Application.Identity;

namespace KSC.MeetingManagement.Api;

/// <summary>Seeds a disposable local development database with the documented starter administrator.</summary>
public static class DevelopmentAdminSeeder
{
    public const string Email = "admin@example.com";
    public const string Password = "Admin123456@";

    public static async Task SeedIfEnabledAsync(
        IServiceProvider services,
        bool isDevelopment,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!isDevelopment || !enabled)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<IdentityAdminService>();
        _ = await admin.SetupAsync(
            new SetupRequest("سازمان نمونه", "local", "Asia/Tehran", Email, "admin", Password),
            cancellationToken);
        // SetupAsync is deliberately one-shot: an existing tenant/user means the local seed is left untouched.
    }
}
