namespace Pinguin.Services;

public class StudyRoom
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Owner { get; set; } = string.Empty;
    public List<string> Members { get; set; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; } // CreatedAt + 3 hours, set at creation
}

public class PendingStudyRoomInvite
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Creator { get; init; } = string.Empty;
    public List<string> InvitedMembers { get; init; } = new();
    public HashSet<string> AcceptedMembers { get; } = new();
    public HashSet<string> DeclinedMembers { get; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; } // CreatedAt + 5 minutes

    public bool AllAccepted => AcceptedMembers.Count == InvitedMembers.Count;
    public bool AnyDeclined => DeclinedMembers.Count > 0;
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
}
