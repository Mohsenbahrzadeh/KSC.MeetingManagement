using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Web.Services;

public sealed record MeetingInput(string Title, string? Description, MeetingMode Mode, DateTimeOffset Start, DateTimeOffset End, bool AllowConflicts);

/// <summary>Typed client for the meetings REST API used by dialogs and the calendar.</summary>
public sealed class MeetingsApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<MeetingDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync(c => c.GetAsync(new Uri($"api/v1/meetings/{id}", UriKind.Relative), cancellationToken), cancellationToken);

    /// <summary>Creates a meeting. The client generates the id so a retried request cannot create a duplicate.</summary>
    public Task<ApiResult<MeetingDto>> CreateAsync(Guid id, MeetingInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SendAsync(
            c => c.PostAsJsonAsync(
                new Uri("api/v1/meetings", UriKind.Relative),
                new { id, input.Title, input.Description, input.Mode, input.Start, input.End, input.AllowConflicts },
                Json,
                cancellationToken),
            cancellationToken);
    }

    public Task<ApiResult<MeetingDto>> UpdateAsync(Guid id, long expectedVersion, MeetingInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SendAsync(
            c => c.PutAsJsonAsync(
                new Uri($"api/v1/meetings/{id}", UriKind.Relative),
                new { expectedVersion, input.Title, input.Description, input.Mode, input.Start, input.End, input.AllowConflicts },
                Json,
                cancellationToken),
            cancellationToken);
    }

    public Task<ApiResult<MeetingDto>> RescheduleAsync(Guid id, long expectedVersion, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        SendAsync(
            c => c.PostAsJsonAsync(
                new Uri($"api/v1/meetings/{id}/reschedule", UriKind.Relative),
                new { expectedVersion, start, end, allowConflicts = false },
                Json,
                cancellationToken),
            cancellationToken);

    private async Task<ApiResult<MeetingDto>> SendAsync(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(Client);
            return await ApiResultReader.ReadAsync<MeetingDto>(response, Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<MeetingDto>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<MeetingDto>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }
}
