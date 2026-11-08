using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSC.MeetingManagement.Application.Participants;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Persian texts for participant states.</summary>
public static class ParticipantText
{
    public static string Status(InvitationStatus status) => status switch
    {
        InvitationStatus.Invited => "دعوت‌شده",
        InvitationStatus.Accepted => "پذیرفته",
        InvitationStatus.Declined => "رد کرده",
        InvitationStatus.Revoked => "لغو دعوت",
        _ => "—",
    };

    public static string Kind(ParticipantKind kind) => kind == ParticipantKind.Guest ? "مهمان" : "عضو سازمان";
}

/// <summary>Typed client for the participants of one meeting.</summary>
public sealed class ParticipantsApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<List<ParticipantDto>>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync<List<ParticipantDto>>(c => c.GetAsync(Uri(meetingId, string.Empty), cancellationToken), cancellationToken);

    public Task<ApiResult<ParticipantDto>> AddInternalAsync(Guid meetingId, Guid userId, bool allowConflicts, CancellationToken cancellationToken) =>
        SendAsync<ParticipantDto>(c => c.PostAsJsonAsync(Uri(meetingId, "/internal"), new { userId, allowConflicts }, Json, cancellationToken), cancellationToken);

    public Task<ApiResult<ParticipantDto>> RevokeAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken) =>
        SendAsync<ParticipantDto>(c => c.PostAsync(Uri(meetingId, $"/{participantId}/revoke"), content: null, cancellationToken), cancellationToken);

    /// <summary>Invites a guest; the raw token in the result is shown only once by the API.</summary>
    public Task<ApiResult<GuestInvitationDto>> InviteGuestAsync(Guid meetingId, string name, string email, DateTimeOffset expiresUtc, CancellationToken cancellationToken) =>
        SendAsync<GuestInvitationDto>(c => c.PostAsJsonAsync(Uri(meetingId, "/guests"), new { name, email, expiresUtc }, Json, cancellationToken), cancellationToken);

    private static Uri Uri(Guid meetingId, string suffix) => new($"api/v1/meetings/{meetingId}/participants{suffix}", UriKind.Relative);

    private async Task<ApiResult<T>> SendAsync<T>(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(Client);
            var result = await ApiResultReader.ReadAsync<T>(response, Json, cancellationToken);
            return result.Error switch
            {
                ApiErrorKind.VersionConflict => ApiResult.Fail<T>(ApiErrorKind.VersionConflict, "این شرکت‌کننده قبلاً اضافه شده است."),
                ApiErrorKind.NotFound => ApiResult.Fail<T>(ApiErrorKind.NotFound, "جلسه یا شرکت‌کننده پیدا نشد."),
                _ => result,
            };
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
