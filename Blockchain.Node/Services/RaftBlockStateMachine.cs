using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using DotNext.IO;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public sealed class RaftBlockStateMachine : SimpleStateMachine
{
    private const int SnapshotSchemaVersion = 1;
    private static readonly byte[] SnapshotCheckpointPayloadBytes = "NEXUS_RAFT_SNAPSHOT_CHECKPOINT_V1"u8.ToArray();

    private readonly RaftCommittedBlockCommandApplier _commandApplier;
    private readonly IBlockchainStore _blockStore;
    private readonly ILogger<RaftBlockStateMachine> _logger;
    private readonly string _snapshotPath;
    private readonly RaftSnapshotOptions _snapshotOptions;
    private long _lastAppliedIndex;
    private long _lastAppliedTerm;
    private long _bytesSinceSnapshot;
    private long? _currentSnapshotIndex;
    private long? _currentSnapshotTerm;
    private string _currentSnapshotChecksum = string.Empty;
    private long? _currentSnapshotSizeBytes;
    private DateTimeOffset? _lastSnapshotCreatedAtUtc;
    private volatile bool _healthy = true;
    private string _failureMessage = string.Empty;

    public RaftBlockStateMachine(
        IOptions<RaftOptions> raftOptions,
        RaftCommittedBlockCommandApplier commandApplier,
        IBlockchainStore blockStore,
        ILogger<RaftBlockStateMachine> logger)
        : base(new DirectoryInfo(Path.GetFullPath(raftOptions.Value.SnapshotPath)))
    {
        _commandApplier = commandApplier;
        _blockStore = blockStore;
        _logger = logger;
        _snapshotPath = Path.GetFullPath(raftOptions.Value.SnapshotPath);
        _snapshotOptions = raftOptions.Value.Snapshot;
    }

    public RaftSnapshotDiagnostics GetDiagnostics()
    {
        PruneSnapshots();
        long? publishedSnapshotIndex = ((ISnapshotManager)this).Snapshot?.Index;
        return new RaftSnapshotDiagnostics(
            _snapshotPath,
            _currentSnapshotIndex,
            publishedSnapshotIndex,
            _currentSnapshotTerm,
            _lastAppliedIndex,
            _lastAppliedTerm,
            _currentSnapshotSizeBytes,
            _currentSnapshotChecksum,
            _lastSnapshotCreatedAtUtc,
            _healthy,
            _failureMessage,
            !_healthy ? "Faulted" : _currentSnapshotIndex != null ? "Available" : "NotCreated");
    }

    public static ReadOnlyMemory<byte> SnapshotCheckpointPayload => SnapshotCheckpointPayloadBytes;

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.IsSnapshot)
        {
            _logger.LogDebug("[Raft] Snapshot entry {Index} reached block state machine.", entry.Index);
            return false;
        }

        byte[] payload;
        try
        {
            payload = CopyPayload(entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[Raft] Failed to read committed log entry payload at index {Index}, term {Term}. The state machine is faulted to prevent divergent state.",
                entry.Index,
                entry.Term);
            MarkFault(entry, ex.Message);
            throw new InvalidDataException($"Unable to read committed Raft entry {entry.Index}.", ex);
        }

        if (payload.Length == 0)
        {
            _logger.LogDebug("[Raft] Empty log entry {Index} reached block state machine.", entry.Index);
            _lastAppliedIndex = Math.Max(_lastAppliedIndex, entry.Index);
            _lastAppliedTerm = Math.Max(_lastAppliedTerm, entry.Term);
            return ShouldCreateSnapshot(entry.Index, 0L);
        }

        if (payload.AsSpan().SequenceEqual(SnapshotCheckpointPayloadBytes))
        {
            _lastAppliedIndex = Math.Max(_lastAppliedIndex, entry.Index);
            _lastAppliedTerm = Math.Max(_lastAppliedTerm, entry.Term);
            _logger.LogInformation("[Raft] Published pending state machine snapshot at checkpoint entry {Index}.", entry.Index);
            return false;
        }

        var result = await _commandApplier.ApplyAsync(payload, entry.Index, entry.Term);
        if (!result.Success)
        {
            _logger.LogCritical(
                "[Raft] Committed block command {Index} was rejected by local validation. The state machine is faulted: {Message}",
                entry.Index,
                result.Message);
            MarkFault(entry, result.Message);
            throw new InvalidOperationException(
                $"Committed Raft command {entry.Index} could not be applied: {result.Message}");
        }

        _lastAppliedIndex = Math.Max(_lastAppliedIndex, entry.Index);
        _lastAppliedTerm = Math.Max(_lastAppliedTerm, entry.Term);
        return ShouldCreateSnapshot(entry.Index, payload.LongLength);
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        var content = BuildSnapshotContent();
        var contentBytes = JsonSerializer.SerializeToUtf8Bytes(content, SnapshotJsonContext.Default.RaftSnapshotContent);
        string checksum = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();
        var file = new RaftSnapshotFile(SnapshotSchemaVersion, checksum, content);
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-snapshot-{Guid.NewGuid():N}.json");

        try
        {
            await using (var output = File.Create(snapshotPath))
            {
                await JsonSerializer.SerializeAsync(output, file, SnapshotJsonContext.Default.RaftSnapshotFile, token);
            }

            await using var input = File.OpenRead(snapshotPath);
            long snapshotSize = input.Length;
            await writer.CopyFromAsync(input, input.Length, token);
            _currentSnapshotIndex = content.LastIncludedLogIndex;
            _currentSnapshotTerm = content.LastIncludedTerm;
            _currentSnapshotChecksum = checksum;
            _currentSnapshotSizeBytes = snapshotSize;
            _lastSnapshotCreatedAtUtc = content.CreatedAtUtc;
            _bytesSinceSnapshot = 0L;
            _logger.LogInformation(
                "[Raft] Persisted state machine snapshot at log index {LastAppliedIndex}, term {LastAppliedTerm}, size {SnapshotSizeBytes} bytes, channels {ChannelCount}, checksum {Checksum}.",
                content.LastIncludedLogIndex,
                content.LastIncludedTerm,
                snapshotSize,
                content.Channels.Count,
                checksum);
        }
        finally
        {
            TryDelete(snapshotPath);
        }
    }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        await using var input = snapshotFile.OpenRead();
        var snapshot = await JsonSerializer.DeserializeAsync(input, SnapshotJsonContext.Default.RaftSnapshotFile, token)
            ?? throw new InvalidDataException("Raft snapshot is empty.");

        if (snapshot.SchemaVersion != SnapshotSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Raft snapshot schema version {snapshot.SchemaVersion}.");
        }

        var contentBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Content, SnapshotJsonContext.Default.RaftSnapshotContent);
        string actualChecksum = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(snapshot.Checksum),
            Convert.FromHexString(actualChecksum)))
        {
            throw new InvalidDataException("Raft snapshot checksum validation failed.");
        }

        foreach (var channel in snapshot.Content.Channels.OrderBy(channel => channel.ChannelId == "System" ? 0 : 1))
        {
            _blockStore.ReplaceChain(channel.ChannelId, channel.Blocks.OrderBy(block => block.Index).ToList());
            foreach (var metadata in channel.FinalityMetadata)
            {
                _blockStore.SaveFinalityMetadata(metadata);
            }

            foreach (var proof in channel.ContributionProofs)
            {
                _blockStore.SaveContributionProof(proof.BlockHash, proof.Proof);
            }
        }

        _lastAppliedIndex = snapshot.Content.LastIncludedLogIndex;
        _lastAppliedTerm = snapshot.Content.LastIncludedTerm;
        _currentSnapshotIndex = snapshot.Content.LastIncludedLogIndex;
        _currentSnapshotTerm = snapshot.Content.LastIncludedTerm;
        _currentSnapshotChecksum = actualChecksum;
        _currentSnapshotSizeBytes = snapshotFile.Length;
        _lastSnapshotCreatedAtUtc = snapshot.Content.CreatedAtUtc;
        _bytesSinceSnapshot = 0L;
        _healthy = true;
        _failureMessage = string.Empty;
        PruneSnapshots();
        _logger.LogInformation(
            "[Raft] Restored state machine snapshot index {LastAppliedIndex}, checksum {Checksum}, channels {ChannelCount}.",
            _lastAppliedIndex,
            actualChecksum,
            snapshot.Content.Channels.Count);
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

    private RaftSnapshotContent BuildSnapshotContent()
    {
        var channels = new List<RaftSnapshotChannel>();
        foreach (var channelId in _blockStore.GetKnownChannels().OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase))
        {
            var blocks = _blockStore.LoadChain(channelId).OrderBy(block => block.Index).ToList();
            var metadata = new List<BlockFinalityMetadata>();
            var proofs = new List<RaftSnapshotContributionProof>();
            foreach (var block in blocks)
            {
                var blockMetadata = _blockStore.GetFinalityMetadata(block.Hash);
                if (blockMetadata != null)
                {
                    metadata.Add(blockMetadata);
                }

                var proof = _blockStore.GetContributionProof(block.Hash);
                if (proof != null)
                {
                    proofs.Add(new RaftSnapshotContributionProof(block.Hash, proof));
                }
            }

            channels.Add(new RaftSnapshotChannel(channelId, blocks, metadata, proofs));
        }

        string chainTipHash = channels
            .SelectMany(channel => channel.Blocks)
            .OrderByDescending(block => block.Index)
            .ThenByDescending(block => block.TimestampUnixSeconds)
            .FirstOrDefault()?.Hash ?? string.Empty;

        return new RaftSnapshotContent(
            SnapshotSchemaVersion,
            _lastAppliedIndex,
            _lastAppliedTerm,
            DateTimeOffset.UtcNow,
            chainTipHash,
            channels);
    }

    private bool ShouldCreateSnapshot(long appliedIndex, long payloadSize)
    {
        if (!_snapshotOptions.Enabled)
        {
            return false;
        }

        _bytesSinceSnapshot = checked(_bytesSinceSnapshot + Math.Max(0L, payloadSize));
        long snapshotIndex = _currentSnapshotIndex ?? 0L;
        bool entryThresholdReached = appliedIndex - snapshotIndex >= Math.Max(1, _snapshotOptions.EntryThreshold);
        bool sizeThresholdReached = _snapshotOptions.SizeThresholdBytes > 0L &&
                                    _bytesSinceSnapshot >= _snapshotOptions.SizeThresholdBytes;
        return entryThresholdReached || sizeThresholdReached;
    }

    private void MarkFault(LogEntry entry, string message)
    {
        _failureMessage = $"Log index {entry.Index}, term {entry.Term}: {message}";
        _healthy = false;
    }

    private void PruneSnapshots()
    {
        int retainCount = Math.Max(1, _snapshotOptions.RetainCount);
        try
        {
            var snapshots = Directory
                .EnumerateFiles(_snapshotPath, "*-*", SearchOption.TopDirectoryOnly)
                .Select(path => new { Path = path, Parts = Path.GetFileName(path).Split('-', 2) })
                .Where(item => item.Parts.Length == 2 &&
                               long.TryParse(item.Parts[0], out _) &&
                               long.TryParse(item.Parts[1], out _))
                .Select(item => new
                {
                    item.Path,
                    Index = long.Parse(item.Parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    Term = long.Parse(item.Parts[1], System.Globalization.CultureInfo.InvariantCulture)
                })
                .OrderByDescending(item => item.Index)
                .ThenByDescending(item => item.Term)
                .Skip(retainCount)
                .ToArray();

            foreach (var snapshot in snapshots)
            {
                TryDelete(snapshot.Path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record RaftSnapshotFile(
    int SchemaVersion,
    string Checksum,
    RaftSnapshotContent Content);

public sealed record RaftSnapshotContent(
    int SchemaVersion,
    long LastIncludedLogIndex,
    long LastIncludedTerm,
    DateTimeOffset CreatedAtUtc,
    string ChainTipHash,
    List<RaftSnapshotChannel> Channels);

public sealed record RaftSnapshotChannel(
    string ChannelId,
    List<Block> Blocks,
    List<BlockFinalityMetadata> FinalityMetadata,
    List<RaftSnapshotContributionProof> ContributionProofs);

public sealed record RaftSnapshotContributionProof(
    string BlockHash,
    ContributionProof Proof);

public sealed record RaftSnapshotDiagnostics(
    string SnapshotPath,
    long? CurrentSnapshotIndex,
    long? PublishedSnapshotIndex,
    long? CurrentSnapshotTerm,
    long LastAppliedIndex,
    long LastAppliedTerm,
    long? SnapshotSizeBytes,
    string SnapshotChecksum,
    DateTimeOffset? LastSnapshotCreatedAtUtc,
    bool StateMachineHealthy,
    string StateMachineFailure,
    string Status);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(RaftSnapshotFile))]
[JsonSerializable(typeof(RaftSnapshotContent))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext
{
}

#pragma warning restore DOTNEXT001
