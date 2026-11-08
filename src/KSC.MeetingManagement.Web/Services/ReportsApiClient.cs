using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Reports;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Typed tenant-scoped reporting endpoints.</summary>
public sealed class ReportsApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<ApiResult<MeetingReport>> MeetingsAsync(DateTimeOffset from, DateTimeOffset to, MeetingStatus? status,
        Guid? organizerId, Guid? committeeId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = new List<string> { $"from={Encode(from)}", $"to={Encode(to)}", $"skip={skip}", $"take={take}" };
        if (status is { } s) query.Add($"status={s}");
        if (organizerId is { } organizer) query.Add($"organizerId={organizer}");
        if (committeeId is { } committee) query.Add($"committeeId={committee}");
        return GetAsync<MeetingReport>("meetings", query, cancellationToken);
    }

    public Task<ApiResult<string>> MeetingsCsvAsync(DateTimeOffset from, DateTimeOffset to, MeetingStatus? status,
        Guid? organizerId, Guid? committeeId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = new List<string> { $"from={Encode(from)}", $"to={Encode(to)}", $"skip={skip}", $"take={take}", "format=csv" };
        if (status is { } s) query.Add($"status={s}");
        if (organizerId is { } organizer) query.Add($"organizerId={organizer}");
        if (committeeId is { } committee) query.Add($"committeeId={committee}");
        return GetCsvAsync("meetings", query, cancellationToken);
    }

    public Task<ApiResult<List<AttendanceRow>>> AttendanceAsync(DateTimeOffset from, DateTimeOffset to, int skip, int take, CancellationToken cancellationToken) =>
        GetAsync<List<AttendanceRow>>("attendance", [$"from={Encode(from)}", $"to={Encode(to)}", $"skip={skip}", $"take={take}"], cancellationToken);

    public Task<ApiResult<ActionsSummary>> ActionsAsync(DateOnly? dueFrom, DateOnly? dueTo, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (dueFrom is { } f) query.Add($"dueFrom={f:yyyy-MM-dd}");
        if (dueTo is { } t) query.Add($"dueTo={t:yyyy-MM-dd}");
        return GetAsync<ActionsSummary>("actions", query, cancellationToken);
    }

    public Task<ApiResult<ResolutionsSummary>> ResolutionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        GetAsync<ResolutionsSummary>("resolutions", [$"from={Encode(from)}", $"to={Encode(to)}"], cancellationToken);

    private async Task<ApiResult<T>> GetAsync<T>(string report, List<string> query, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(new Uri($"api/v1/reports/{report}?{string.Join('&', query)}", UriKind.Relative), cancellationToken);
            return await ApiResultReader.ReadAsync<T>(response, Json, cancellationToken);
        }
        catch (HttpRequestException) { return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }

    private async Task<ApiResult<string>> GetCsvAsync(string report, List<string> query, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(new Uri($"api/v1/reports/{report}?{string.Join('&', query)}", UriKind.Relative), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await ApiResultReader.ReadAsync<JsonElement>(response, Json, cancellationToken);
                return ApiResult.Fail<string>(failure.Error, failure.Message ?? ApiResultReader.GenericFailure);
            }
            return ApiResult.Ok(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException) { return ApiResult.Fail<string>(ApiErrorKind.Failure, ApiResultReader.GenericFailure); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<string>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }

    private static string Encode(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);
}
