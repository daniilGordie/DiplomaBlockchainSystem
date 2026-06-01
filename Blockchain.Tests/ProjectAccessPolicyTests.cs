using Blockchain.Application.Security;

namespace Blockchain.Tests;

public class ProjectAccessPolicyTests
{
    [Fact]
    public void CanReadProject_ShouldAllowSystemWithoutRole()
    {
        var policy = new ProjectAccessPolicy();

        Assert.True(policy.CanReadProject("System", "None"));
    }

    [Fact]
    public void CanReadProject_ShouldRejectProjectWithoutRole()
    {
        var policy = new ProjectAccessPolicy();

        Assert.False(policy.CanReadProject("ProjectA", "None"));
    }

    [Fact]
    public void CanReadProject_ShouldAllowProjectRole()
    {
        var policy = new ProjectAccessPolicy();

        Assert.True(policy.CanReadProject("ProjectA", "Developer"));
    }
}
