using KSC.MeetingManagement.Application.Meetings;
using Microsoft.AspNetCore.Mvc;

namespace KSC.MeetingManagement.Api.Controllers;

/// <summary>Builds the 409 response for a time conflict: a readable message plus the conflicting meetings so the UI can show them.</summary>
internal static class ConflictProblem
{
    public static ObjectResult From(MeetingTimeConflictException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var first = exception.Conflicts[0];
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "تداخل زمانی",
            Detail = exception.Conflicts.Count == 1
                ? $"این زمان با جلسه «{first.Title}» تداخل دارد."
                : $"این زمان با {exception.Conflicts.Count} جلسهٔ دیگر تداخل دارد.",
            Type = "https://ksc.example/problems/meeting-time-conflict",
        };
        problem.Extensions["conflicts"] = exception.Conflicts;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict, ContentTypes = { "application/problem+json" } };
    }
}
