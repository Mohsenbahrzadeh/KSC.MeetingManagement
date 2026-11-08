using System.Net.Http.Json;
using System.Text.Json;
using KSC.MeetingManagement.Application.Attachments;
using Microsoft.AspNetCore.Components.Forms;

namespace KSC.MeetingManagement.Web.Services;

/// <summary>Meeting attachments through the authenticated, tenant-scoped API client.</summary>
public sealed class AttachmentsApiClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private HttpClient Client => httpClientFactory.CreateClient(ApiMeetingCalendarDataProvider.ClientName);

    public Task<ApiResult<List<AttachmentDto>>> ListAsync(Guid meetingId, CancellationToken cancellationToken) =>
        SendAsync<List<AttachmentDto>>(c => c.GetAsync(Uri(meetingId), cancellationToken), cancellationToken);

    public Task<ApiResult<AttachmentDto>> UploadAsync(Guid meetingId, IBrowserFile file, CancellationToken cancellationToken) =>
        SendAsync<AttachmentDto>(async c =>
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StreamContent(file.OpenReadStream(AttachmentOptions.DefaultMaxBytes, cancellationToken)), "file", file.Name);
            return await c.PostAsync(Uri(meetingId), content, cancellationToken);
        }, cancellationToken);

    public Task<ApiResult<bool>> DeleteAsync(Guid meetingId, Guid id, CancellationToken cancellationToken) =>
        SendResultAsync<bool>(async c =>
        {
            using var response = await c.DeleteAsync(Uri(meetingId, id), cancellationToken);
            if (response.IsSuccessStatusCode) return ApiResult.Ok(true);
            return await ApiResultReader.ReadAsync<bool>(response, Json, cancellationToken);
        }, cancellationToken);

    public Task<ApiResult<AttachmentFile>> DownloadAsync(Guid meetingId, Guid id, CancellationToken cancellationToken) =>
        SendResultAsync<AttachmentFile>(async c =>
        {
            using var response = await c.GetAsync(Uri(meetingId, id), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return await ApiResultReader.ReadAsync<AttachmentFile>(response, Json, cancellationToken);

            // Bound the server-side buffer too: the endpoint normally serves at most 10 MiB.
            if (response.Content.Headers.ContentLength > AttachmentOptions.DefaultMaxBytes)
                return ApiResult.Fail<AttachmentFile>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > AttachmentOptions.DefaultMaxBytes)
                    return ApiResult.Fail<AttachmentFile>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
                buffer.Write(chunk, 0, read);
            }
            return ApiResult.Ok(new AttachmentFile(buffer.ToArray()));
        }, cancellationToken);

    private static Uri Uri(Guid meetingId, Guid? id = null) =>
        new($"api/v1/meetings/{meetingId}/attachments" + (id is { } value ? $"/{value}" : ""), UriKind.Relative);

    private async Task<ApiResult<T>> SendAsync<T>(Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken cancellationToken) =>
        await SendResultAsync<T>(async c =>
        {
            using var response = await send(c);
            return await ApiResultReader.ReadAsync<T>(response, Json, cancellationToken);
        }, cancellationToken);

    private async Task<ApiResult<T>> SendResultAsync<T>(Func<HttpClient, Task<ApiResult<T>>> send, CancellationToken cancellationToken)
    {
        try { return await send(Client); }
        catch (HttpRequestException) { return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure); }
        catch (IOException) { return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult.Fail<T>(ApiErrorKind.Failure, ApiResultReader.GenericFailure);
        }
    }
}

public sealed record AttachmentFile(byte[] Content);
