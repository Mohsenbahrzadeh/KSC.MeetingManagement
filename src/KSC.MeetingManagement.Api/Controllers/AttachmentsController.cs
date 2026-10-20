using Microsoft.AspNetCore.RateLimiting;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Attachments;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/attachments")]
[RequiresTenant]
public sealed class AttachmentsController(AttachmentService attachments) : ControllerBase
{
    private const long RequestLimit = AttachmentOptions.DefaultMaxBytes + (1024 * 1024);

    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> List(Guid meetingId, CancellationToken cancellationToken) =>
        Ok(await attachments.ListAsync(meetingId, cancellationToken));

    [EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
    [Audited("attachment.upload", "attachment", "meetingId")]
    [HttpPost]
    [HasPermission(Permissions.MeetingsEdit)]
    [RequestSizeLimit(RequestLimit)]
    public async Task<IActionResult> Upload(Guid meetingId, IFormFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            await using var stream = file.OpenReadStream();
            return await attachments.UploadAsync(meetingId, file.FileName, stream, cancellationToken) is { } dto
                ? StatusCode(StatusCodes.Status201Created, dto)
                : NotFound();
        }
        catch (AttachmentTooLargeException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    [EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
    [Audited("attachment.download", "attachment", "meetingId")]
    [HttpGet("{attachmentId:guid}")]
    [HasPermission(Permissions.MeetingsView)]
    public async Task<IActionResult> Download(Guid meetingId, Guid attachmentId, CancellationToken cancellationToken)
    {
        if (await attachments.DownloadAsync(meetingId, attachmentId, cancellationToken) is not { } download)
        {
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(download.Content, download.ContentType, download.FileName);
    }

    [Audited("attachment.delete", "attachment", "meetingId")]
    [HttpDelete("{attachmentId:guid}")]
    [HasPermission(Permissions.MeetingsEdit)]
    public async Task<IActionResult> Delete(Guid meetingId, Guid attachmentId, CancellationToken cancellationToken) =>
        await attachments.DeleteAsync(meetingId, attachmentId, cancellationToken) ? NoContent() : NotFound();
}
