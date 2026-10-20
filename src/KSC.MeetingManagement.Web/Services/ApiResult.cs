using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace KSC.MeetingManagement.Web.Services;

public enum ApiErrorKind
{
    None = 0,
    Validation,
    TimeConflict,
    VersionConflict,
    Forbidden,
    NotFound,
    Failure,
}

/// <summary>Outcome of an API call with a user-presentable message that never contains server internals.</summary>
public sealed record ApiResult<T>(T? Value, ApiErrorKind Error = ApiErrorKind.None, string? Message = null)
{
    public bool Succeeded => Error == ApiErrorKind.None;
}

/// <summary>Factory methods for <see cref="ApiResult{T}"/>.</summary>
public static class ApiResult
{
    public static ApiResult<T> Ok<T>(T value) => new(value);

    public static ApiResult<T> Fail<T>(ApiErrorKind kind, string message) => new(default, kind, message);
}

public static class ApiResultReader
{
    public const string GenericFailure = "عملیات انجام نشد. لطفاً دوباره تلاش کنید.";

    /// <summary>Maps an HTTP response to a result. Details written by our own API are shown for validation and time conflicts; anything else gets a generic text.</summary>
    public static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);

        if (response.IsSuccessStatusCode)
        {
            T? value;
            try
            {
                value = await response.Content.ReadFromJsonAsync<T>(options, cancellationToken);
            }
            catch (JsonException)
            {
                value = default;
            }

            return value is null ? ApiResult.Fail<T>(ApiErrorKind.Failure, GenericFailure) : ApiResult.Ok<T>(value);
        }

        var (detail, hasConflicts) = await ReadProblemAsync(response, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Conflict when hasConflicts => ApiResult.Fail<T>(ApiErrorKind.TimeConflict, detail ?? "این زمان با جلسهٔ دیگری تداخل دارد."),
            HttpStatusCode.Conflict => ApiResult.Fail<T>(ApiErrorKind.VersionConflict, "این جلسه توسط کاربر دیگری تغییر کرده است. صفحه را تازه کنید و دوباره تلاش کنید."),
            HttpStatusCode.UnprocessableEntity => ApiResult.Fail<T>(ApiErrorKind.Validation, detail ?? "اطلاعات واردشده معتبر نیست."),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ApiResult.Fail<T>(ApiErrorKind.Forbidden, "شما دسترسی لازم برای این کار را ندارید."),
            HttpStatusCode.NotFound => ApiResult.Fail<T>(ApiErrorKind.NotFound, "جلسه پیدا نشد."),
            _ => ApiResult.Fail<T>(ApiErrorKind.Failure, GenericFailure),
        };
    }

    private static async Task<(string? Detail, bool HasConflicts)> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return (detail, root.TryGetProperty("conflicts", out _));
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }
}
