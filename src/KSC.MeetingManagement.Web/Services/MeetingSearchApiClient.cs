using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Meetings;

namespace KSC.MeetingManagement.Web.Services;

public sealed class MeetingSearchApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ApiResult<MeetingSearchResult>> SearchAsync(string text, int skip, int take, CancellationToken cancellationToken)
    {
        var query = $"q={Uri.EscapeDataString(text)}&skip={skip}&take={take}";
        try
        {
            using var response = await Client.GetAsync(new Uri("api/v1/meetings/search?" + query, UriKind.Relative), cancellationToken);
            return await ApiResultReader.ReadAsync<MeetingSearchResult>(response, Json, cancellationToken);
        }
        catch (HttpRequestException) { return ApiResult.Fail<MeetingSearchResult>(ApiErrorKind.Failure, ApiResultReader.GenericFailure); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<MeetingSearchResult>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);
}
