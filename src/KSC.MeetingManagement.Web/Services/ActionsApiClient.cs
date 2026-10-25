using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Actions;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Typed requests to the authenticated, tenant-scoped actions API.</summary>
public sealed class ActionsApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<List<ActionItemDto>>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync<List<ActionItemDto>>(c => c.GetAsync(Uri(meetingId), cancellationToken), cancellationToken);

    public Task<ApiResult<ActionItemDto>> CreateAsync(Guid meetingId, ActionItemInput input, CancellationToken cancellationToken) =>
        SendAsync<ActionItemDto>(c => c.PostAsJsonAsync(Uri(meetingId), input, Json, cancellationToken), cancellationToken);

    public Task<ApiResult<ActionItemDto>> UpdateAsync(Guid meetingId, Guid id, long version, ActionItemInput input, CancellationToken cancellationToken) =>
        SendAsync<ActionItemDto>(c => c.PutAsJsonAsync(Uri(meetingId, id),
            new { expectedLockVersion = version, input.Title, input.Description, input.OwnerUserId, input.DueDate }, Json, cancellationToken), cancellationToken);

    public Task<ApiResult<ActionItemDto>> StartAsync(Guid meetingId, Guid id, long version, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, id, "start", new { expectedLockVersion = version }, cancellationToken);

    public Task<ApiResult<ActionItemDto>> CompleteAsync(Guid meetingId, Guid id, long version, string? note, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, id, "complete", new { expectedLockVersion = version, note }, cancellationToken);

    public Task<ApiResult<ActionItemDto>> CancelAsync(Guid meetingId, Guid id, long version, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, id, "cancel", new { expectedLockVersion = version, reason }, cancellationToken);

    private Task<ApiResult<ActionItemDto>> TransitionAsync(Guid meetingId, Guid id, string action, object body, CancellationToken cancellationToken) =>
        SendAsync<ActionItemDto>(c => c.PostAsJsonAsync(Uri(meetingId, id, action), body, Json, cancellationToken), cancellationToken);

    private static Uri Uri(Guid meetingId, Guid? id = null, string? action = null) =>
        new($"api/v1/meetings/{meetingId}/actions" + (id is { } value ? $"/{value}" : "") + (action is null ? "" : $"/{action}"), UriKind.Relative);

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
