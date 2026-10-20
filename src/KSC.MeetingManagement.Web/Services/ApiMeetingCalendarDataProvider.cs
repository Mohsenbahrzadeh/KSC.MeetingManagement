using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Calendar;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Loads calendar items from the REST API for exactly the requested UTC window, following pages until it is complete.</summary>
public sealed class ApiMeetingCalendarDataProvider(IHttpClientFactory httpClientFactory) : IMeetingCalendarDataProvider
{
    public const string ClientName = "api";
    private const int PageSize = 200;
    private const int MaxPages = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<CalendarItem>> GetItemsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CalendarFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var client = httpClientFactory.CreateClient(ClientName);
        var items = new List<CalendarItem>();

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"api/v1/meetings?from={Uri.EscapeDataString(fromUtc.ToString("O"))}&to={Uri.EscapeDataString(toUtc.ToString("O"))}"
                + $"&skip={page * PageSize}&take={PageSize}"
                + (filter.Status is { } status ? $"&status={status}" : string.Empty);

            using var response = await client.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
            response.EnsureSuccessStatusCode();

            var meetings = await response.Content.ReadFromJsonAsync<List<MeetingDto>>(Json, cancellationToken) ?? [];
            items.AddRange(meetings
                .Where(m => m.StartUtc is not null && m.EndUtc is not null)
                .Select(m => new CalendarItem(m.Id, m.Title, m.StartUtc!.Value, m.EndUtc!.Value, (CalendarItemStatus)(int)m.Status, m.Version)));

            if (meetings.Count < PageSize)
            {
                break;
            }
        }

        return items;
    }
}
