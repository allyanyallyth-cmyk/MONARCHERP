namespace Monarch.Core.Security;

public sealed class UserSession
{
    public Guid UserId { get; }
    public string Username { get; }
    public string DisplayName { get; }
    public string RoleCode { get; }
    public IReadOnlyCollection<string> Permissions { get; }

    public UserSession(Guid userId, string username, string displayName, string roleCode, IEnumerable<string> permissions)
    {
        UserId = userId;
        Username = username;
        DisplayName = displayName;
        RoleCode = roleCode;
        Permissions = permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

public sealed class UserSummary
{
    public Guid Id { get; }
    public string Username { get; }
    public string DisplayName { get; }
    public string RoleCode { get; }
    public bool IsActive { get; }
    public DateTime CreatedUtc { get; }

    public UserSummary(Guid id, string username, string displayName, string roleCode, bool isActive, DateTime createdUtc)
    { Id = id; Username = username; DisplayName = displayName; RoleCode = roleCode; IsActive = isActive; CreatedUtc = createdUtc; }
}
