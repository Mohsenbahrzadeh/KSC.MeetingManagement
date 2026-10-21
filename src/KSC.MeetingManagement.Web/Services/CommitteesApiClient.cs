using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Committees;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Typed client for the committees REST API.</summary>
public sealed class CommitteesApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<List<CommitteeDto>>> ListAsync(CancellationToken cancellationToken) =>
        SendAsync<List<CommitteeDto>>(c => c.GetAsync(new Uri("api/v1/committees", UriKind.Relative), cancellationToken), cancellationToken);

    public Task<ApiResult<CommitteeDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        SendAsync<CommitteeDto>(c => c.GetAsync(new Uri($"api/v1/committees/{id}", UriKind.Relative), cancellationToken), cancellationToken);

    public Task<ApiResult<CommitteeDto>> CreateAsync(string name, string? description, CancellationToken cancellationToken) =>
        SendAsync<CommitteeDto>(
            c => c.PostAsJsonAsync(new Uri("api/v1/committees", UriKind.Relative), new { name, description }, Json, cancellationToken),
            cancellationToken);

    public Task<ApiResult<CommitteeDto>> UpdateAsync(Guid id, string name, string? description, bool isActive, CancellationToken cancellationToken) =>
        SendAsync<CommitteeDto>(
            c => c.PutAsJsonAsync(new Uri($"api/v1/committees/{id}", UriKind.Relative), new { name, description, isActive }, Json, cancellationToken),
            cancellationToken);

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
