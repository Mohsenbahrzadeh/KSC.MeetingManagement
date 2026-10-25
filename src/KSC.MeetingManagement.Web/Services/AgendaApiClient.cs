using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Agenda;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Typed agenda requests through the authenticated, tenant-scoped API client.</summary>
public sealed class AgendaApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<AgendaDto>> GetAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync(c => c.GetAsync(Uri(meetingId), cancellationToken), cancellationToken);

    public Task<ApiResult<AgendaDto>> AddAsync(Guid meetingId, long revision, AgendaItemInput input, CancellationToken cancellationToken) =>
        SendAsync(c => c.PostAsJsonAsync(Uri(meetingId), Request(revision, input), Json, cancellationToken), cancellationToken);

    public Task<ApiResult<AgendaDto>> UpdateAsync(Guid meetingId, Guid itemId, long revision, AgendaItemInput input, CancellationToken cancellationToken) =>
        SendAsync(c => c.PutAsJsonAsync(Uri(meetingId, itemId.ToString()), Request(revision, input), Json, cancellationToken), cancellationToken);

    public Task<ApiResult<AgendaDto>> RemoveAsync(Guid meetingId, Guid itemId, long revision, CancellationToken cancellationToken) =>
        SendAsync(c => c.DeleteAsync(Uri(meetingId, $"{itemId}?expectedRevision={revision}"), cancellationToken), cancellationToken);

    public Task<ApiResult<AgendaDto>> ReorderAsync(Guid meetingId, long revision, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) =>
        SendAsync(c => c.PutAsJsonAsync(Uri(meetingId, "order"), new { expectedRevision = revision, itemIds = ids }, Json, cancellationToken), cancellationToken);

    private static object Request(long revision, AgendaItemInput input) =>
        new { expectedRevision = revision, input.Title, input.Description, input.PresenterUserId, input.DurationMinutes };

    private static Uri Uri(Guid meetingId, string? suffix = null) =>
        new($"api/v1/meetings/{meetingId}/agenda" + (suffix is null ? "" : $"/{suffix}"), UriKind.Relative);

    private async Task<ApiResult<AgendaDto>> SendAsync(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(Client);
            return await ApiResultReader.ReadAsync<AgendaDto>(response, Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<AgendaDto>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<AgendaDto>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }
}
