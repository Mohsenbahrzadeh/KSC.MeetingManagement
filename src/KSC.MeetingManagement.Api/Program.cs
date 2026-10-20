using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Audit;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Application.Tenants;
using KSC.MeetingManagement.Infrastructure.Persistence;
using KSC.MeetingManagement.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<ITenantStore>(sp => sp.GetRequiredService<InMemoryTenantStore>());

builder.Services.AddSingleton<InMemoryMembershipStore>();
builder.Services.AddSingleton<IMembershipStore>(sp => sp.GetRequiredService<InMemoryMembershipStore>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IRequestInfo, HttpRequestInfo>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();
builder.Services.AddKscPersistence(builder.Configuration);
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
