using Blockchain.Application.Blocks;
using System.Text.Json;

namespace Blockchain.Node.Services;

public sealed class RaftCommittedBlockCommandApplier
{
    private const string CommitBlockProposalCommandType = "CommitBlockProposal";

    private readonly CommittedBlockApplier _committedBlockApplier;
    private readonly ILogger<RaftCommittedBlockCommandApplier> _logger;

    public RaftCommittedBlockCommandApplier(
        CommittedBlockApplier committedBlockApplier,
        ILogger<RaftCommittedBlockCommandApplier> logger)
    {
        _committedBlockApplier = committedBlockApplier;
        _logger = logger;
    }

    public async Task<BlockWriteResult> ApplyAsync(ReadOnlyMemory<byte> payload, long raftLogIndex)
    {
        RaftBlockCommitCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<RaftBlockCommitCommand>(payload.Span);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Raft] Rejected committed log entry with invalid block command payload.");
            return new BlockWriteResult(false, "Invalid Raft block command payload", "System");
        }

        if (command == null)
        {
            return new BlockWriteResult(false, "Empty Raft block command payload", "System");
        }

        if (!string.Equals(command.CommandType, CommitBlockProposalCommandType, StringComparison.Ordinal))
        {
            _logger.LogWarning("[Raft] Rejected unsupported committed command type {CommandType}.", command.CommandType);
            return new BlockWriteResult(false, $"Unsupported Raft command '{command.CommandType}'", command.ChannelId);
        }

        var block = command.ToBlock();
        var model = command.ToBlockModel();

        return await _committedBlockApplier.ApplyAsync(
            block,
            model,
            broadcastToPeers: false,
            finalityMetadata: new Blockchain.Core.BlockFinalityMetadata(
                block.Hash,
                block.ChannelId,
                ConsensusFinalityModes.Raft,
                raftLogIndex,
                null,
                DateTime.UtcNow));
    }
}
