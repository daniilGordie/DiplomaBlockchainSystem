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

    private readonly RaftCommittedBlockCommandApplier _commandApplier;
    private readonly IBlockchainStore _blockStore;
    private readonly ILogger<RaftBlockStateMachine> _logger;
    private readonly string _snapshotPath;
    private long _lastAppliedIndex;
    private long _lastAppliedTerm;
    private long? _currentSnapshotIndex;
    private long? _currentSnapshotTerm;
    private string _currentSnapshotChecksum = string.Empty;
    private long? _currentSnapshotSizeBytes;
    private DateTimeOffset? _lastSnapshotCreatedAtUtc;

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
    }

    public RaftSnapshotDiagnostics GetDiagnostics()
    {
        return new RaftSnapshotDiagnostics(
            _snapshotPath,
            _currentSnapshotIndex,
            _currentSnapshotTerm,
            _lastAppliedIndex,
            _lastAppliedTerm,
            _currentSnapshotSizeBytes,
            _currentSnapshotChecksum,
            _lastSnapshotCreatedAtUtc,
            _currentSnapshotIndex != null ? "Available" : "NotCreated");
    }

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.IsSnapshot)
        {
            _logger.LogDebug("[Raft] Snapshot entry {Index} reached block state machine.", entry.Index);
            return false;
        }

        var payload = CopyPayload(entry);
        if (payload.Length == 0)
        {
            _logger.LogDebug("[Raft] Empty log entry {Index} reached block state machine.", entry.Index);
            _lastAppliedIndex = Math.Max(_lastAppliedIndex, entry.Index);
            _lastAppliedTerm = Math.Max(_lastAppliedTerm, entry.Term);
            return false;
        }

        var result = await _commandApplier.ApplyAsync(payload, entry.Index);
        if (!result.Success)
        {
            _logger.LogWarning(
                "[Raft] Committed block command {Index} was rejected by local validation and will be skipped: {Message}",
                entry.Index,
                result.Message);
        }

        _lastAppliedIndex = Math.Max(_lastAppliedIndex, entry.Index);
        _lastAppliedTerm = Math.Max(_lastAppliedTerm, entry.Term);
        return result.Success;
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
    long? CurrentSnapshotTerm,
    long LastAppliedIndex,
    long LastAppliedTerm,
    long? SnapshotSizeBytes,
    string SnapshotChecksum,
    DateTimeOffset? LastSnapshotCreatedAtUtc,
    string Status);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(RaftSnapshotFile))]
[JsonSerializable(typeof(RaftSnapshotContent))]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext
{
}

#pragma warning restore DOTNEXT001
