namespace Blockchain.Application.Security;

public sealed class ProjectAccessPolicy
{
    public bool CanReadProject(string projectId, string role)
    {
        return string.Equals(projectId, "System", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(role, "None", StringComparison.OrdinalIgnoreCase);
    }
}
