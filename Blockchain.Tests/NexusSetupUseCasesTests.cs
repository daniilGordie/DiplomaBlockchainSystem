using Blockchain.Application.Setup;
using Blockchain.Node.Services;
using System.Text.Json;

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

    [Fact]
    public void JoinNetwork_ShouldNotPersistConsensusModeSwitches()
    {
        var setup = new NexusSetupUseCases();
        var created = setup.CreateNetwork(new CreateNetworkSetupRequest(
            "Test Network",
            "all-in-one",
            "test-network",
            "node-a",
            "http://localhost:7041",
            "http://localhost:7141",
            "Tcp",
            "node-a:6041",
            null,
            null));

        var joined = setup.JoinNetwork(new JoinNetworkSetupRequest(
            JsonSerializer.Serialize(created.Invite),
            "Edge",
            "edge-a"));

        Assert.True(joined.Success, joined.Message);
        Assert.DoesNotContain(joined.Configuration.Keys, key => key.Contains("FinalityMode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(joined.Configuration.Keys, key => key.Contains("EnableProof", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConsensusPolicy_ShouldDeriveFinalityFromNodeRole()
    {
        Assert.Equal(ConsensusFinalityModes.Raft, ConsensusPolicy.GetFinalityMode(new NexusNodeOptions { Role = "Consensus" }));
        Assert.Equal(ConsensusFinalityModes.Raft, ConsensusPolicy.GetFinalityMode(new NexusNodeOptions { Role = "Edge" }));
        Assert.Equal(ConsensusFinalityModes.Immediate, ConsensusPolicy.GetFinalityMode(new NexusNodeOptions { Role = "Local" }));
        Assert.Equal(ConsensusPolicy.NetworkEngine, ConsensusPolicy.GetEngineName(new NexusNodeOptions { Role = "Bootstrap" }));
    }
}
