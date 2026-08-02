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
    public void JoinNetwork_ShouldEnableProofOfContributionUsingRuntimeConfigurationKey()
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
        Assert.Equal("true", joined.Configuration["Consensus:EnableProofOfContributionValidation"]);
        Assert.False(joined.Configuration.ContainsKey("Consensus:EnablePoC"));
        Assert.Equal("Raft", joined.Configuration["Consensus:FinalityMode"]);
    }

    [Fact]
    public void ConsensusPolicy_ShouldRejectEveryLegacyNetworkBypass()
    {
        var consensus = new ConsensusOptions
        {
            FinalityMode = ConsensusFinalityModes.Immediate,
            EnableProofOfContributionValidation = false,
            RequireProofOfWork = true,
            AcceptP2PBlocksAsFinal = true
        };

        var errors = ConsensusConfigurationPolicy.Validate(
            consensus,
            new NexusNodeOptions { Role = "Consensus" });

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, error => error.Contains("FinalityMode=Raft", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("EnableProofOfContributionValidation", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("RequireProofOfWork", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("AcceptP2PBlocksAsFinal", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsensusPolicy_ShouldAllowImmediateFinalityOnlyForIsolatedLocalNode()
    {
        var errors = ConsensusConfigurationPolicy.Validate(
            new ConsensusOptions
            {
                FinalityMode = ConsensusFinalityModes.Immediate,
                EnableProofOfContributionValidation = false,
                RequireProofOfWork = false,
                AcceptP2PBlocksAsFinal = false
            },
            new NexusNodeOptions { Role = "Local" });

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ConsensusValidationService_ShouldFailStartupForLegacyNetworkConfiguration()
    {
        var service = new ConsensusConfigurationValidationService(
            Microsoft.Extensions.Options.Options.Create(new ConsensusOptions
            {
                FinalityMode = ConsensusFinalityModes.Immediate,
                EnableProofOfContributionValidation = false,
                RequireProofOfWork = true,
                AcceptP2PBlocksAsFinal = true
            }),
            Microsoft.Extensions.Options.Options.Create(new NexusNodeOptions { Role = "Bootstrap" }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Contains("Invalid consensus configuration", error.Message, StringComparison.Ordinal);
        Assert.Contains("DotNext Raft", error.Message, StringComparison.Ordinal);
    }
}
