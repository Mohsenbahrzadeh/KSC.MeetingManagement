using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Security;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Chat;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KSC.MeetingManagement.Api.Controllers;

public sealed record SendChatMessageRequest(string Text);

[ApiController]
[Route("api/v1/meetings/{meetingId:guid}/chat")]
[RequiresTenant]
public sealed class ChatController(ChatService chat, IAuthorizationService authorization) : ControllerBase
{
    /// <summary>Messages after afterSequence (0 for all), oldest first. Only the organizer and invited members.</summary>
    [HttpGet]
    [HasPermission(Permissions.MeetingsView)]
    public Task<IActionResult> List(Guid meetingId, long afterSequence = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Run(async () => await chat.ListAsync(meetingId, afterSequence, take, cancellationToken) is { } list ? Ok(list) : NotFound());

    [HttpPost]
    [HasPermission(Permissions.MeetingsView)]
    [EnableRateLimiting(SecurityExtensions.SensitivePolicy)]
    [Audited("chat.send", "chat", "meetingId")]
    public Task<IActionResult> Send(Guid meetingId, SendChatMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(async () => await chat.SendAsync(meetingId, request.Text, cancellationToken) is { } dto ? StatusCode(StatusCodes.Status201Created, dto) : NotFound());
    }

    /// <summary>The sender deletes their own message; the host (meetings.edit) can delete any message.</summary>
    [HttpDelete("{messageId:guid}")]
    [HasPermission(Permissions.MeetingsView)]
    [Audited("chat.delete", "chat", "meetingId")]
    public Task<IActionResult> Delete(Guid meetingId, Guid messageId, CancellationToken cancellationToken) =>
        Run(async () =>
        {
            var moderator = (await authorization.AuthorizeAsync(User, null, HasPermissionAttribute.PolicyPrefix + Permissions.MeetingsEdit)).Succeeded;
            return await chat.DeleteAsync(meetingId, messageId, moderator, cancellationToken) is { } dto ? Ok(dto) : NotFound();
        });

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ChatForbiddenException)
        {
            return Problem("شما عضو گفت‌وگوی این جلسه نیستید.", statusCode: StatusCodes.Status403Forbidden);
        }
        catch (MeetingDomainException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
