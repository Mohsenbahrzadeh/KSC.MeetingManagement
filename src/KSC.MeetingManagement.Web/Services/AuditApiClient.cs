using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Audit;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Search the tenant's audit log through the authenticated API client.</summary>
public sealed class AuditApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ApiResult<List<AuditEventDto>>> SearchAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new List<string> { $"skip={query.Skip}", $"take={query.Take}" };
        if (query.From is { } from) parameters.Add($"from={Uri.EscapeDataString(from.ToString("O"))}");
        if (query.To is { } to) parameters.Add($"to={Uri.EscapeDataString(to.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(query.Action)) parameters.Add($"action={Uri.EscapeDataString(query.Action)}");
        if (query.ActorId is { } actor) parameters.Add($"actorId={actor}");
        if (query.ResourceId is { } resource) parameters.Add($"resourceId={resource}");
        if (query.Outcome is { } outcome) parameters.Add($"outcome={outcome}");

        try
        {
            using var response = await httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName)
                .GetAsync(new Uri("api/v1/audit?" + string.Join('&', parameters), UriKind.Relative), cancellationToken);
            return await ApiResultReader.ReadAsync<List<AuditEventDto>>(response, Json, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Fail<List<AuditEventDto>>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<List<AuditEventDto>>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }
}
