namespace KSC.MeetingManagement.Application.Identity;

/// <summary>Authenticated caller of the current request.</summary>
public interface ICurrentUser
{
    Guid RequiredUserId { get; }
}
