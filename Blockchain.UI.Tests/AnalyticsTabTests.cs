using Bunit;
using Blockchain.Node;
using Blockchain.UI.Components.Dashboard;
using Blockchain.UI.Models;
using Xunit;

namespace Blockchain.UI.Tests;

public sealed class AnalyticsTabTests : TestContext
{
    [Fact]
    public void AnalyticsTab_ShouldRenderAuditTrailRows()
    {
        var analytics = new AnalyticsResponse
        {
            TotalBlocks = 5,
            TotalTasks = 2,
            TasksDone = 1
        };

        var audit = new SecurityAuditResponse
        {
            ChainValid = true,
            CheckedBlocks = 5
        };

        var trail = new List<AuditTrailEntry>
        {
            new() { BlockIndex = 11, BlockHash = "abc123", Timestamp = "2026-05-12T10:00:00Z", EventType = "Create", Actor = "Alice", EntityId = "TSK-1" },
            new() { BlockIndex = 12, BlockHash = "def456", Timestamp = "2026-05-12T10:01:00Z", EventType = "Update", Actor = "Bob", EntityId = "TSK-1" }
        };

        var cut = RenderComponent<AnalyticsTab>(parameters => parameters
            .Add(p => p.Data, analytics)
            .Add(p => p.Audit, audit)
            .Add(p => p.CurrentUserName, "Alice")
            .Add(p => p.AuditTrail, trail));

        Assert.Contains("Blockchain Audit Trail", cut.Markup);
        Assert.Contains("Alice", cut.Markup);
        Assert.Contains("Bob", cut.Markup);
        Assert.DoesNotContain("No blockchain events available", cut.Markup);
    }

    [Fact]
    public void AnalyticsTab_ShouldRenderEmptyStateForMissingTrail()
    {
        var cut = RenderComponent<AnalyticsTab>(parameters => parameters
            .Add(p => p.Data, new AnalyticsResponse())
            .Add(p => p.Audit, new SecurityAuditResponse())
            .Add(p => p.AuditTrail, new List<AuditTrailEntry>()));

        Assert.Contains("No blockchain events available for the selected project.", cut.Markup);
    }
}
