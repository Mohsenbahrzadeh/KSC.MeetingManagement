using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Web.Components;
using KSC.MeetingManagement.Web.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// REST API used by the pages. Requests carry the signed-in user's token and chosen organisation (ApiAuthHandler);
// the auth client has no handler because its calls create the session.
builder.Services.AddSingleton<CircuitServicesAccessor>();
builder.Services.AddScoped<CircuitHandler, ServicesAccessorCircuitHandler>();
builder.Services.AddTransient<ApiAuthHandler>();
static void ConfigureApi(IServiceProvider sp, HttpClient client) =>
    client.BaseAddress = new Uri(sp.GetRequiredService<IConfiguration>()["Api:BaseUrl"] ?? "http://localhost:5000/");
builder.Services.AddHttpClient(ApiMeetingCalendarDataProvider.ClientName, ConfigureApi).AddHttpMessageHandler<ApiAuthHandler>();
builder.Services.AddHttpClient(AuthApiClient.ClientName, ConfigureApi);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuthApiClient>();
builder.Services.AddScoped<ISessionStore, ProtectedSessionStore>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<RoomApiClient>();
builder.Services.AddScoped<IMeetingCalendarDataProvider, ApiMeetingCalendarDataProvider>();
builder.Services.AddScoped<MeetingsApiClient>();
builder.Services.AddScoped<CommitteesApiClient>();
builder.Services.AddScoped<UsersApiClient>();
builder.Services.AddScoped<AgendaApiClient>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseMiddleware<KSC.MeetingManagement.Web.Security.WebSecurityHeadersMiddleware>();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
