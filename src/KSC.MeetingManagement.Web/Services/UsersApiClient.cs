using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Identity;
using KSC.MeetingManagement.Domain.Identity;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Persian texts for the role names used by the API.</summary>
public static class RoleText
{
    public static string Label(string role) => role switch
    {
        Roles.TenantOwner => "مالک سازمان",
        Roles.TenantAdministrator => "مدیر سازمان",
        Roles.WorkflowAdministrator => "مدیر گردش‌کار",
        Roles.MeetingOrganizer => "برگزارکنندهٔ جلسه",
        Roles.Secretary => "دبیر",
        Roles.Participant => "شرکت‌کننده",
        Roles.Approver => "تأییدکننده",
        Roles.Auditor => "ممیز",
        _ => role,
    };
}

/// <summary>Typed client for the member administration API (needs the users.manage permission).</summary>
public sealed class UsersApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<List<MemberDto>>> ListAsync(CancellationToken cancellationToken) =>
        SendAsync<List<MemberDto>>(c => c.GetAsync(new Uri("api/v1/users", UriKind.Relative), cancellationToken), cancellationToken);

    public Task<ApiResult<MemberDto>> AddAsync(string email, string displayName, string password, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        SendAsync<MemberDto>(
            c => c.PostAsJsonAsync(new Uri("api/v1/users", UriKind.Relative), new { email, displayName, password, roles }, Json, cancellationToken),
            cancellationToken);

    public Task<ApiResult<MemberDto>> SetRolesAsync(Guid userId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken) =>
        SendAsync<MemberDto>(
            c => c.PutAsJsonAsync(new Uri($"api/v1/users/{userId}/roles", UriKind.Relative), new { roles }, Json, cancellationToken),
            cancellationToken);

    public Task<ApiResult<MemberDto>> SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken) =>
        SendAsync<MemberDto>(
            c => c.PutAsJsonAsync(new Uri($"api/v1/users/{userId}/active", UriKind.Relative), new { active }, Json, cancellationToken),
            cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(Client);
            var result = await ApiResultReader.ReadAsync<T>(response, Json, cancellationToken);
            return result.Error switch
            {
                ApiErrorKind.VersionConflict => ApiResult.Fail<T>(ApiErrorKind.VersionConflict, "این ایمیل قبلاً در این سازمان ثبت شده یا کاربر تغییر کرده است."),
                ApiErrorKind.NotFound => ApiResult.Fail<T>(ApiErrorKind.NotFound, "کاربر پیدا نشد."),
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
