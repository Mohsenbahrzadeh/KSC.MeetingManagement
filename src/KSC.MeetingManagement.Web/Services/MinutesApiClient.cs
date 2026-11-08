using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Minutes;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Minutes requests use the authenticated API client; the server enforces tenant and permission policies.</summary>
public sealed class MinutesApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<MinutesDto>> GetAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync<MinutesDto>(c => c.GetAsync(Uri(meetingId), cancellationToken), cancellationToken);

    public Task<ApiResult<List<MinutesVersionSummaryDto>>> VersionsAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync<List<MinutesVersionSummaryDto>>(c => c.GetAsync(Uri(meetingId, "versions"), cancellationToken), cancellationToken);

    public Task<ApiResult<MinutesVersionDto>> VersionAsync(Guid meetingId, int number, CancellationToken cancellationToken) =>
        SendAsync<MinutesVersionDto>(c => c.GetAsync(Uri(meetingId, $"versions/{number}"), cancellationToken), cancellationToken);

    public Task<ApiResult<MinutesDto>> SaveAsync(Guid meetingId, int expectedVersionNumber, string content, CancellationToken cancellationToken) =>
        SendAsync<MinutesDto>(c => c.PutAsJsonAsync(Uri(meetingId), new { content, expectedVersionNumber }, Json, cancellationToken), cancellationToken);

    public Task<ApiResult<MinutesDto>> SubmitAsync(Guid meetingId, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, "submit", null, cancellationToken);

    public Task<ApiResult<MinutesDto>> ApproveAsync(Guid meetingId, string? comment, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, "approve", comment, cancellationToken);

    public Task<ApiResult<MinutesDto>> RejectAsync(Guid meetingId, string comment, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, "reject", comment, cancellationToken);

    public Task<ApiResult<MinutesDto>> ReopenAsync(Guid meetingId, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(meetingId, "reopen", reason, cancellationToken);

    private Task<ApiResult<MinutesDto>> TransitionAsync(Guid meetingId, string action, string? comment, CancellationToken cancellationToken) =>
        SendAsync<MinutesDto>(c => c.PostAsJsonAsync(Uri(meetingId, action), new { comment }, Json, cancellationToken), cancellationToken);

    private static Uri Uri(Guid meetingId, string? suffix = null) =>
        new($"api/v1/meetings/{meetingId}/minutes" + (suffix is null ? "" : $"/{suffix}"), UriKind.Relative);

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
