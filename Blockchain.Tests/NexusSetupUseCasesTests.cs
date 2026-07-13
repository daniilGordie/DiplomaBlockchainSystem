using Blockchain.Application.Setup;

public sealed class NexusSetupUseCasesTests
{
    [Fact]
    public void CreateNetwork_ShouldCreateIrohRaftConfigurationWithoutTcpEndpoint()
    {
        var setup = new NexusSetupUseCases();

        var result = setup.CreateNetwork(new CreateNetworkSetupRequest(
            "Test Network",
            "all-in-one",
            "test-network",
            "node-a",
            "http://localhost:7041",
            "http://localhost:7141",
            "Iroh",
            null,
            "iroh-node-a",
            "iroh://iroh-node-a"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Iroh", result.Configuration["Raft:Transport"]);
        Assert.Equal("iroh-node-a", result.Configuration["Raft:IrohNodeId"]);
        Assert.Equal(string.Empty, result.Configuration["Raft:PublicEndPoint"]);
        Assert.NotNull(result.Invite);
        Assert.Contains("Iroh", result.Invite!.SupportedRaftTransports);
    }

    [Fact]
    public void JoinNetwork_ShouldRejectInvalidInvite()
    {
        var setup = new NexusSetupUseCases();

        var result = setup.JoinNetwork(new JoinNetworkSetupRequest("not-an-invite", "Edge", null));

        Assert.False(result.Success);
        Assert.Equal("ConfigurationError", result.NextState);
    }
}
