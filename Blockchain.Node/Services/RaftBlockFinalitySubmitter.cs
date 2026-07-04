using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using DotNext.Net.Cluster.Consensus.Raft;
using System.Text.Json;

namespace Blockchain.Node.Services;

public sealed class RaftBlockFinalitySubmitter : IBlockFinalitySubmitter
{
    private readonly IRaftCommandReplicator _replicator;
    private readonly ILogger<RaftBlockFinalitySubmitter> _logger;

    public RaftBlockFinalitySubmitter(
        IRaftCommandReplicator replicator,
        ILogger<RaftBlockFinalitySubmitter> logger)
    {
        _replicator = replicator;
        _logger = logger;
    }

    public async Task<BlockWriteResult> SubmitAsync(BlockProposal proposal, BlockModel sourceModel)
    {
        _ = sourceModel;
        var command = RaftBlockCommitCommand.FromProposal(proposal);
        bool committed;

        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command);
            committed = await _replicator.ReplicateAsync(payload, proposal.Block.Hash, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[Raft] Block proposal {BlockHash} was not accepted by the current node.", proposal.Block.Hash);
            return new BlockWriteResult(false, "Raft leader is unavailable on this node", proposal.Block.ChannelId);
        }

        if (!committed)
        {
            return new BlockWriteResult(false, "Raft majority commit was not reached", proposal.Block.ChannelId);
        }

        return new BlockWriteResult(true, "Raft majority commit reached", proposal.Block.ChannelId);
    }
}

public interface IRaftCommandReplicator
{
    Task<bool> ReplicateAsync(ReadOnlyMemory<byte> payload, string context, CancellationToken token);
}

public sealed class DotNextRaftCommandReplicator : IRaftCommandReplicator
{
    private readonly IRaftCluster _raftCluster;

    public DotNextRaftCommandReplicator(IRaftCluster raftCluster)
    {
        _raftCluster = raftCluster;
    }

    public Task<bool> ReplicateAsync(ReadOnlyMemory<byte> payload, string context, CancellationToken token)
    {
        return _raftCluster.ReplicateAsync(payload, context, token).AsTask();
    }
}

public sealed record RaftBlockCommitCommand(
    string CommandType,
    string ChannelId,
    int Index,
    string Hash,
    string PreviousHash,
    string ValidatorPublicKey,
    string Signature,
    long Nonce,
    long TimestampUnixSeconds,
    string Data,
    RaftContributionProofCommand ContributionProof)
{
    public static RaftBlockCommitCommand FromProposal(BlockProposal proposal)
    {
        var block = proposal.Block;
        var proof = proposal.ContributionProof;

        return new RaftBlockCommitCommand(
            "CommitBlockProposal",
            block.ChannelId,
            block.Index,
            block.Hash,
            block.PreviousHash,
            block.ValidatorPublicKey,
            block.Signature,
            block.Nonce,
            block.TimestampUnixSeconds,
            block.Data,
            new RaftContributionProofCommand(
                proof.ProjectId,
                proof.Epoch,
                proof.ProducerPublicKey,
                proof.ProducerScore,
                proof.ScoreSnapshotHash,
                proof.EvidenceBlockHashes.ToArray()));
    }

    public Block ToBlock()
    {
        return new Block
        {
            ChannelId = ChannelId,
            Index = Index,
            Hash = Hash,
            PreviousHash = PreviousHash,
            ValidatorPublicKey = ValidatorPublicKey,
            Signature = Signature,
            Nonce = Nonce,
            TimestampUnixSeconds = TimestampUnixSeconds,
            Timestamp = TimestampUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(TimestampUnixSeconds).UtcDateTime
                : DateTime.MinValue,
            Data = Data
        };
    }

    public ContributionProof ToContributionProof()
    {
        return new ContributionProof(
            ContributionProof.ProjectId,
            ContributionProof.Epoch,
            ContributionProof.ProducerPublicKey,
            ContributionProof.ProducerScore,
            ContributionProof.ScoreSnapshotHash,
            ContributionProof.EvidenceBlockHashes.ToArray());
    }

    public BlockModel ToBlockModel()
    {
        var model = GrpcProjectMapper.ToBlockModel(ToBlock());
        model.ContributionProof = GrpcProjectMapper.ToContributionProofModel(ToContributionProof());
        return model;
    }
}

public sealed record RaftContributionProofCommand(
    string ProjectId,
    long Epoch,
    string ProducerPublicKey,
    int ProducerScore,
    string ScoreSnapshotHash,
    IReadOnlyList<string> EvidenceBlockHashes);
