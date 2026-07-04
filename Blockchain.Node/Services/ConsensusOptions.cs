namespace Blockchain.Node.Services;

public sealed class ConsensusOptions
{
    public bool EnableProofOfContributionValidation { get; set; }
    public bool RequireProofOfWork { get; set; } = true;
    public bool AcceptP2PBlocksAsFinal { get; set; } = true;
    public string FinalityMode { get; set; } = ConsensusFinalityModes.Immediate;
}

public static class ConsensusFinalityModes
{
    public const string Immediate = "Immediate";
    public const string Raft = "Raft";
}
