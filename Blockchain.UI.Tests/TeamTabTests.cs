using Bunit;
using Blockchain.UI.Components.Dashboard;
using Xunit;

namespace Blockchain.UI.Tests;

public sealed class TeamTabTests : TestContext
{
    [Fact]
    public void TeamTab_ShouldExposePublicKeyAndFingerprintInputs()
    {
        var cut = RenderComponent<TeamTab>(parameters => parameters
            .Add(p => p.CurrentProjectId, "Alpha_123")
            .Add(p => p.NewMemberName, "Bob")
            .Add(p => p.NewMemberRole, "Developer")
            .Add(p => p.NewMemberPublicKey, "")
            .Add(p => p.NewMemberPublicKeyFingerprint, "")
            .Add(p => p.IsLoggedIn, true));

        Assert.Contains("Member public key", cut.Markup);
        Assert.Contains("Expected key fingerprint", cut.Markup);
    }
}
