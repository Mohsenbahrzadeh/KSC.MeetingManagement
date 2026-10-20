using System.Text;
using KSC.MeetingManagement.Api.Authorization;
using KSC.MeetingManagement.Api.Observability;
using KSC.MeetingManagement.Api.Tenancy;
using KSC.MeetingManagement.Application.Reports;
using KSC.MeetingManagement.Domain.Identity;
using KSC.MeetingManagement.Domain.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

/// <summary>Read-only reports of the current organisation. All require reports.view.</summary>
[ApiController]
[Route("api/v1/reports")]
[RequiresTenant]
[HasPermission(Permissions.ReportsView)]
public sealed class ReportsController(ReportService reports) : ControllerBase
{
    /// <summary>Meetings starting inside [from, to) with status counts. Add format=csv for a spreadsheet file.</summary>
    [HttpGet("meetings")]
    [Audited("report.meetings", "report", "none")]
    public Task<IActionResult> Meetings(
        DateTimeOffset from,
        DateTimeOffset to,
        MeetingStatus? status,
        Guid? organizerId,
        Guid? committeeId,
        string? format,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var report = await reports.MeetingsAsync(new MeetingReportQuery(from, to, status, organizerId, committeeId, skip, take), cancellationToken);
            return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
                ? File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReportService.ToCsv(report))).ToArray(), "text/csv; charset=utf-8", "meetings.csv")
                : Ok(report);
        });

    /// <summary>Invitation responses per meeting (invited, accepted, declined, revoked, guests).</summary>
    [HttpGet("attendance")]
    [Audited("report.attendance", "report", "none")]
    public Task<IActionResult> Attendance(DateTimeOffset from, DateTimeOffset to, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await reports.AttendanceAsync(from, to, skip, take, cancellationToken)));

    /// <summary>Follow-up actions by status, overdue count and owner, optionally for a due-date window.</summary>
    [HttpGet("actions")]
    [Audited("report.actions", "report", "none")]
    public Task<IActionResult> Actions(DateOnly? dueFrom, DateOnly? dueTo, CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await reports.ActionsAsync(dueFrom, dueTo, cancellationToken)));

    /// <summary>Resolutions of meetings in the range, by status.</summary>
    [HttpGet("resolutions")]
    [Audited("report.resolutions", "report", "none")]
    public Task<IActionResult> Resolutions(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await reports.ResolutionsAsync(from, to, cancellationToken)));

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
