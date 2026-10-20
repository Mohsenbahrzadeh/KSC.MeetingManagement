using KSC.MeetingManagement.Application.Actions;
using KSC.MeetingManagement.Application.Meetings;
using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.Application.Reports;

public sealed record MeetingReportQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    MeetingStatus? Status,
    Guid? OrganizerId,
    Guid? CommitteeId,
    int Skip,
    int Take);

public sealed record MeetingReportRow(
    Guid Id,
    string? Number,
    string Title,
    MeetingStatus Status,
    MeetingMode Mode,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    Guid OrganizerId,
    Guid? CommitteeId);

public sealed record MeetingReport(
    int TotalCount,
    IReadOnlyDictionary<MeetingStatus, int> ByStatus,
    IReadOnlyList<MeetingReportRow> Rows);

/// <summary>Invitation responses of one meeting. Actual online presence is a separate report once presence tracking exists.</summary>
public sealed record AttendanceRow(
    Guid MeetingId,
    string? Number,
    string Title,
    DateTimeOffset? StartUtc,
    int Invited,
    int Accepted,
    int Declined,
    int Revoked,
    int Guests);

public sealed record OwnerActionSummary(Guid OwnerUserId, int Active, int Overdue, int Done);

public sealed record ActionsSummary(
    int Total, int Open, int InProgress, int Done, int Cancelled, int Overdue, IReadOnlyList<OwnerActionSummary> ByOwner);

public sealed record ResolutionsSummary(int Total, int Draft, int Adopted, int Rejected, int Revoked);

/// <summary>Read-only aggregation port. Implementations must only read the current tenant's data.</summary>
public interface IReportRepository
{
    Task<MeetingReport> MeetingsAsync(MeetingReportQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyList<AttendanceRow>> AttendanceAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int skip, int take, CancellationToken cancellationToken);

    Task<ActionsSummary> ActionsAsync(DateOnly today, DateOnly? dueFrom, DateOnly? dueTo, CancellationToken cancellationToken);

    Task<ResolutionsSummary> ResolutionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
}

public sealed class ReportService(IReportRepository repository, ActionItemService actions)
{
    public const int MaxPageSize = 500;
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public Task<MeetingReport> MeetingsAsync(MeetingReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRange(query.From, query.To);
        return repository.MeetingsAsync(query with { Skip = Math.Max(0, query.Skip), Take = Math.Clamp(query.Take, 1, MaxPageSize) }, cancellationToken);
    }

    public Task<IReadOnlyList<AttendanceRow>> AttendanceAsync(DateTimeOffset from, DateTimeOffset to, int skip, int take, CancellationToken cancellationToken)
    {
        ValidateRange(from, to);
        return repository.AttendanceAsync(from, to, Math.Max(0, skip), Math.Clamp(take, 1, MaxPageSize), cancellationToken);
    }

    /// <summary>Action counts by status and overdue count (overdue measured at today in the tenant time zone), optionally for a due-date window.</summary>
    public Task<ActionsSummary> ActionsAsync(DateOnly? dueFrom, DateOnly? dueTo, CancellationToken cancellationToken)
    {
        if (dueFrom is { } f && dueTo is { } t && t < f)
        {
            throw new ArgumentException("Range end cannot be before its start.");
        }

        return repository.ActionsAsync(actions.Today(), dueFrom, dueTo, cancellationToken);
    }

    public Task<ResolutionsSummary> ResolutionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        ValidateRange(from, to);
        return repository.ResolutionsAsync(from, to, cancellationToken);
    }

    /// <summary>Spreadsheet text for the meetings report. Cells that could be read as formulas are neutralised.</summary>
    public static string ToCsv(MeetingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string> { "Number,Title,Status,Mode,StartUtc,EndUtc,OrganizerId,CommitteeId" };
        lines.AddRange(report.Rows.Select(r => string.Join(
            ',',
            Cell(r.Number), Cell(r.Title), Cell(r.Status.ToString()), Cell(r.Mode.ToString()),
            Cell(r.StartUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            Cell(r.EndUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            Cell(r.OrganizerId.ToString()), Cell(r.CommitteeId?.ToString()))));
        return string.Join("\r\n", lines) + "\r\n";
    }

    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A leading = + - @ (or tab/CR) makes spreadsheet programs evaluate the cell as a formula.
        var safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return safe.Contains(',', StringComparison.Ordinal) || safe.Contains('"', StringComparison.Ordinal)
            || safe.Contains('\n', StringComparison.Ordinal) || safe.Contains('\r', StringComparison.Ordinal)
            ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : safe;
    }

    private static void ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            throw new ArgumentException("Range end must be after its start.");
        }

        if (to - from > MaxRange)
        {
            throw new ArgumentException($"The range cannot be longer than {MaxRange.TotalDays:0} days.");
        }
    }
}
