using Blockchain.Node.Services;
using Xunit;

namespace Blockchain.Tests;

public sealed class ReplayGuardTests
{
    [Fact]
    public void TryRegister_ShouldRejectDuplicateWithinTtl()
    {
        var guard = new WebhookReplayGuard(TimeSpan.FromMinutes(30));
        DateTime now = DateTime.UtcNow;

        bool first = guard.TryRegister("project:commit:user", now);
        bool second = guard.TryRegister("project:commit:user", now.AddMinutes(1));

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void TryRegister_ShouldAllowSameKeyAfterTtlExpiration()
    {
        var guard = new WebhookReplayGuard(TimeSpan.FromMinutes(30));
        DateTime now = DateTime.UtcNow;

        Assert.True(guard.TryRegister("project:commit:user", now));
        guard.SweepExpired(now.AddHours(1));
        Assert.True(guard.TryRegister("project:commit:user", now.AddHours(1).AddSeconds(1)));
    }
}
