using KSC.MeetingManagement.Calendar;
using KSC.MeetingManagement.Web.Components;
using KSC.MeetingManagement.Web.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// REST API used by the pages. TenantId is a development stand-in until sign-in (ADR-0003) provides the tenant.
builder.Services.AddHttpClient(ApiMeetingCalendarDataProvider.ClientName, (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Api:BaseUrl"] ?? "http://localhost:5000/");
    if (!string.IsNullOrWhiteSpace(config["Api:TenantId"]))
    {
        client.DefaultRequestHeaders.Add("X-Tenant-Id", config["Api:TenantId"]);
    }
});
builder.Services.AddScoped<IMeetingCalendarDataProvider, ApiMeetingCalendarDataProvider>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
