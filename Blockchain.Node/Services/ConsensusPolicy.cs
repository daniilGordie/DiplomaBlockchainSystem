namespace Blockchain.Node.Services;

public static class ConsensusFinalityModes
{
    public const string Immediate = "Immediate";
    public const string Raft = "Raft";
}

public static class ConsensusPolicy
{
    public const string NetworkEngine = "Proof of Contribution + DotNext Raft";
    public const string LocalEngine = "Local PoC validation";

    public static string GetEngineName(NexusNodeOptions node) =>
        node.IsLocal ? LocalEngine : NetworkEngine;

    public static string GetFinalityMode(NexusNodeOptions node) =>
        node.IsLocal ? ConsensusFinalityModes.Immediate : ConsensusFinalityModes.Raft;
}
