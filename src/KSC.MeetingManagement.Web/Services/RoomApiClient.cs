using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSC.MeetingManagement.Web.Services;

public sealed record IceServerDto(string[] Urls, string Username, string Credential);

public sealed record JoinRoomDto(string ServerUrl, string Token, string Room, string Role, DateTimeOffset ExpiresAtUtc, string PresenterIdentity, IceServerDto[]? IceServers);

public enum JoinOutcome
{
    Granted,
    Waiting,
    NotAllowed,
    NotFound,
    NotOpen,
    NotOnline,
    Unavailable,
    Failed,
}

public sealed record JoinResult(JoinOutcome Outcome, JoinRoomDto? Room)
{
    /// <summary>Persian text for the user; empty when joining worked.</summary>
    public string Message => Outcome switch
    {
        JoinOutcome.Granted => string.Empty,
        JoinOutcome.Waiting => "منتظر پذیرش میزبان هستید…",
        JoinOutcome.NotAllowed => "شما اجازهٔ ورود به این جلسه را ندارید.",
        JoinOutcome.NotFound => "جلسه پیدا نشد.",
        JoinOutcome.NotOpen => "اتاق جلسه اکنون باز نیست. کمی قبل از شروع جلسه باز می‌شود.",
        JoinOutcome.NotOnline => "این جلسه حضوری است و اتاق آنلاین ندارد.",
        JoinOutcome.Unavailable => "سرویس جلسهٔ آنلاین در دسترس نیست. با مدیر سامانه تماس بگیرید.",
        _ => ApiResultReader.GenericFailure,
    };
}

/// <summary>Asks the API for permission and a token to enter the room of a meeting.</summary>
public sealed class RoomApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<JoinResult> JoinAsync(Guid meetingId, string? displayName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName)
                .PostAsJsonAsync(new Uri($"api/v1/meetings/{meetingId}/room/join", UriKind.Relative), new { displayName }, Json, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var room = await response.Content.ReadFromJsonAsync<JoinRoomDto>(Json, cancellationToken);
                    return room is null ? new JoinResult(JoinOutcome.Failed, null) : new JoinResult(JoinOutcome.Granted, room);
                case HttpStatusCode.Accepted:
                    return new JoinResult(JoinOutcome.Waiting, null);
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.Unauthorized:
                    return new JoinResult(JoinOutcome.NotAllowed, null);
                case HttpStatusCode.NotFound:
                    return new JoinResult(JoinOutcome.NotFound, null);
                case HttpStatusCode.Conflict:
                    return new JoinResult(JoinOutcome.NotOpen, null);
                case HttpStatusCode.UnprocessableEntity:
                    return new JoinResult(JoinOutcome.NotOnline, null);
                case HttpStatusCode.ServiceUnavailable:
                    return new JoinResult(JoinOutcome.Unavailable, null);
                default:
                    return new JoinResult(JoinOutcome.Failed, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new JoinResult(JoinOutcome.Failed, null);
        }
    }

    /// <summary>Asks the API to mute every other participant's microphone in this meeting.</summary>
    public async Task<string?> MuteAllAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName)
                .PostAsync(new Uri($"api/v1/meetings/{meetingId}/room/mute-all", UriKind.Relative), content: null, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => null,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "فقط مدیر یا برگزارکنندهٔ جلسه می‌تواند میکروفون همه را بی‌صدا کند.",
                HttpStatusCode.NotFound => "جلسه پیدا نشد.",
                HttpStatusCode.ServiceUnavailable => "کنترل میکروفون شرکت‌کنندگان در دسترس نیست. دوباره تلاش کنید.",
                _ => "بی‌صدا کردن شرکت‌کنندگان انجام نشد. دوباره تلاش کنید.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "ارتباط با سرویس جلسه برقرار نشد. دوباره تلاش کنید.";
        }
    }

    public async Task<string?> SetScreenSharePermissionAsync(Guid meetingId, string identity, bool allowed, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName)
                .PutAsJsonAsync(new Uri($"api/v1/meetings/{meetingId}/room/participants/{Uri.EscapeDataString(identity)}/screen-share-permission", UriKind.Relative), new { allowed }, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => null,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "فقط برگزارکننده می‌تواند مجوز اشتراک صفحه را مدیریت کند.",
                HttpStatusCode.NotFound => "جلسه یا شرکت‌کننده پیدا نشد.",
                HttpStatusCode.ServiceUnavailable => "سرویس رسانه برای تغییر مجوز در دسترس نیست.",
                _ => "تغییر مجوز اشتراک صفحه انجام نشد.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "ارتباط با سرویس جلسه برقرار نشد.";
        }
    }

    public async Task<string?> SetRecordingAsync(Guid meetingId, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName)
                .PostAsync(new Uri($"api/v1/meetings/{meetingId}/room/recording/{(enabled ? "start" : "stop")}", UriKind.Relative), null, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => null,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "فقط برگزارکننده می‌تواند ضبط جلسه را کنترل کند.",
                HttpStatusCode.NotFound => "جلسه یا ضبط فعال پیدا نشد.",
                HttpStatusCode.ServiceUnavailable => "قابلیت ضبط سمت سرور پیکربندی یا در دسترس نیست.",
                _ => "تغییر وضعیت ضبط انجام نشد.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "ارتباط با سرویس جلسه برقرار نشد.";
        }
    }
}
