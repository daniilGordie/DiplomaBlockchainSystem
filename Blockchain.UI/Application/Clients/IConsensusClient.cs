using Blockchain.Node;

namespace Blockchain.UI.Application.Clients;

public interface IConsensusClient
{
    Task<ConsensusProducerInfo> GetProducerInfoAsync(string nodeUrl, string projectId);
}

public sealed record ConsensusProducerInfo(
    ContributionProofModel? ContributionProof);
