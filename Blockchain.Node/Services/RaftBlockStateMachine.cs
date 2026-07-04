using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using System.Buffers;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public sealed class RaftBlockStateMachine : IStateMachine
{
    private readonly RaftCommittedBlockCommandApplier _commandApplier;
    private readonly ILogger<RaftBlockStateMachine> _logger;

    public RaftBlockStateMachine(
        RaftCommittedBlockCommandApplier commandApplier,
        ILogger<RaftBlockStateMachine> logger)
    {
        _commandApplier = commandApplier;
        _logger = logger;
    }

    public ISnapshot Snapshot => null!;

    public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.IsSnapshot)
        {
            _logger.LogDebug("[Raft] Snapshot entry {Index} reached block state machine.", entry.Index);
            return entry.Index;
        }

        var payload = CopyPayload(entry);
        if (payload.Length == 0)
        {
            _logger.LogDebug("[Raft] Empty log entry {Index} reached block state machine.", entry.Index);
            return entry.Index;
        }

        var result = await _commandApplier.ApplyAsync(payload, entry.Index);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Raft committed block command {entry.Index} was rejected: {result.Message}");
        }

        return entry.Index;
    }

    private static byte[] CopyPayload(LogEntry entry)
    {
        if (entry.TryGetPayload(out ReadOnlySequence<byte> sequence))
        {
            return sequence.ToArray();
        }

        using var stream = new MemoryStream();
        foreach (var segment in entry.GetPayload())
        {
            stream.Write(segment.Span);
        }

        return stream.ToArray();
    }

    public ValueTask ReclaimGarbageAsync(long index, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }
}

#pragma warning restore DOTNEXT001
