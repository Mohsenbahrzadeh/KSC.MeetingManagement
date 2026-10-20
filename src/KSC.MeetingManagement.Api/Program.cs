using KSC.MeetingManagement.Api.Authentication;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Api.Notifications;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.ValidateKscConfiguration();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddSingleton<InMemoryTenantStore>();
builder.Services.AddScoped<EfTenantStore>();
builder.Services.AddScoped<ITenantStore, CompositeTenantStore>();

builder.Services.AddSingleton<InMemoryMembershipStore>();
builder.Services.AddScoped<EfMembershipStore>();
builder.Services.AddScoped<IMembershipStore, CompositeMembershipStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddKscRateLimiting(builder.Configuration);
builder.Services.AddKscCors(builder.Configuration);
builder.Services.AddKscHealth();
builder.Services.AddScoped<IRequestInfo, HttpRequestInfo>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();
builder.Services.AddKscPersistence(builder.Configuration);
builder.Services.AddSingleton<INotificationDateFormatter, JalaliNotificationDateFormatter>();
builder.Services.AddHostedService<OutboxWorker>();
var jwt = JwtSettings.From(builder.Configuration);
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(new AuthOptions());
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
builder.Services.AddScoped<IUserRepository, EfUserRepository>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.EffectiveKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors(SecurityExtensions.CorsPolicyName);
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();

// After authentication so the "sensitive" limit can be applied per signed-in user.
app.UseRateLimiter();
app.UseAuthorization();
// Start collecting at startup so early measurements are not missed.
_ = app.Services.GetRequiredService<MetricsCollector>();

app.MapControllers();
app.MapKscHealth();
app.MapKscMetrics(app.Configuration);

app.Run();

public partial class Program;
