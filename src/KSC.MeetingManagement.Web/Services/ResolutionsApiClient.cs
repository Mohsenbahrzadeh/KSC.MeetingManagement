using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Resolutions;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Meeting-scoped resolutions through the authenticated API client.</summary>
public sealed class ResolutionsApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<List<ResolutionDto>>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync<List<ResolutionDto>>(c => c.GetAsync(Uri(meetingId), cancellationToken), cancellationToken);

    public Task<ApiResult<ResolutionDto>> CreateAsync(Guid meetingId, string text, Guid? agendaItemId, CancellationToken cancellationToken) =>
        SendAsync<ResolutionDto>(c => c.PostAsJsonAsync(Uri(meetingId), new { text, agendaItemId }, Json, cancellationToken), cancellationToken);

    public Task<ApiResult<ResolutionDto>> EditAsync(Guid meetingId, Guid id, long version, string text, Guid? agendaItemId, CancellationToken cancellationToken) =>
        SendAsync<ResolutionDto>(c => c.PutAsJsonAsync(Uri(meetingId, id), new { expectedLockVersion = version, text, agendaItemId }, Json, cancellationToken), cancellationToken);

    public Task<ApiResult<ResolutionDto>> AdoptAsync(Guid meetingId, Guid id, long version, CancellationToken cancellationToken) =>
        DecideAsync(meetingId, id, "adopt", version, cancellationToken);

    public Task<ApiResult<ResolutionDto>> RejectAsync(Guid meetingId, Guid id, long version, CancellationToken cancellationToken) =>
        DecideAsync(meetingId, id, "reject", version, cancellationToken);

    public Task<ApiResult<ResolutionDto>> RevokeAsync(Guid meetingId, Guid id, long version, string reason, CancellationToken cancellationToken) =>
        SendAsync<ResolutionDto>(c => c.PostAsJsonAsync(Uri(meetingId, id, "revoke"), new { expectedLockVersion = version, reason }, Json, cancellationToken), cancellationToken);

    private Task<ApiResult<ResolutionDto>> DecideAsync(Guid meetingId, Guid id, string action, long version, CancellationToken cancellationToken) =>
        SendAsync<ResolutionDto>(c => c.PostAsJsonAsync(Uri(meetingId, id, action), new { expectedLockVersion = version }, Json, cancellationToken), cancellationToken);

    private static Uri Uri(Guid meetingId, Guid? id = null, string? action = null) =>
        new($"api/v1/meetings/{meetingId}/resolutions" + (id is { } value ? $"/{value}" : "") + (action is null ? "" : $"/{action}"), UriKind.Relative);

    private async Task<ApiResult<T>> SendAsync<T>(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(Client);
            return await ApiResultReader.ReadAsync<T>(response, Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }
}
