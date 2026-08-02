using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Core.Constants;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node;
using Blockchain.Node.Services;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blockchain.Tests;

#pragma warning disable DOTNEXT001

public sealed class RaftBlockFinalitySubmitterTests
{
    [Fact]
    public void RaftBlockCommitCommand_ShouldPreserveProposalAndProof()
    {
        var block = new Block
        {
            Index = 12,
            ChannelId = "ProjectA",
            Hash = "block-hash",
            PreviousHash = "previous-hash",
            ValidatorPublicKey = "producer-pk",
            Signature = "signature",
            Nonce = 7,
            TimestampUnixSeconds = 12345,
            Data = "{\"Type\":\"Update\"}"
        };
        var proof = new ContributionProof(
            "ProjectA",
            12,
            "producer-pk",
            42,
            "snapshot-hash",
            new[] { "evidence-1", "evidence-2" });

        var command = RaftBlockCommitCommand.FromProposal(new BlockProposal(block, proof, DateTime.UtcNow));

        Assert.Equal("CommitBlockProposal", command.CommandType);
        Assert.Equal(block.ChannelId, command.ChannelId);
        Assert.Equal(block.Index, command.Index);
        Assert.Equal(block.Hash, command.Hash);
        Assert.Equal(proof.ProducerPublicKey, command.ContributionProof.ProducerPublicKey);
        Assert.Equal(proof.EvidenceBlockHashes, command.ContributionProof.EvidenceBlockHashes);

        var restoredBlock = command.ToBlock();
        var restoredProof = command.ToContributionProof();
        var restoredModel = command.ToBlockModel();

        Assert.Equal(block.ChannelId, restoredBlock.ChannelId);
        Assert.Equal(block.Index, restoredBlock.Index);
        Assert.Equal(block.Hash, restoredBlock.Hash);
        Assert.Equal(block.TimestampUnixSeconds, restoredBlock.TimestampUnixSeconds);
        Assert.Equal(proof.ProjectId, restoredProof.ProjectId);
        Assert.Equal(proof.ScoreSnapshotHash, restoredProof.ScoreSnapshotHash);
        Assert.NotNull(restoredModel.ContributionProof);
        Assert.Equal(proof.Epoch, restoredModel.ContributionProof.Epoch);
    }

    [Fact]
    public async Task SubmitAsync_ShouldReturnSuccessAfterRaftCommitWithoutLocalApply()
    {
        var block = new Block
        {
            Index = 1,
            ChannelId = "ProjectA",
            Hash = "block-hash",
            PreviousHash = "previous-hash",
            ValidatorPublicKey = "producer-pk",
            Signature = "signature",
            TimestampUnixSeconds = 12345,
            Data = "{\"Type\":\"Update\"}"
        };
        var proof = new ContributionProof(
            "ProjectA",
            1,
            "producer-pk",
            10,
            "snapshot-hash",
            Array.Empty<string>());
        var proposal = new BlockProposal(block, proof, DateTime.UtcNow);
        var replicator = new CapturingRaftCommandReplicator(committed: true);
        var submitter = new RaftBlockFinalitySubmitter(
            replicator,
            Options.Create(new RaftOptions()),
            NullLogger<RaftBlockFinalitySubmitter>.Instance);

        var result = await submitter.SubmitAsync(proposal, new BlockModel { ChannelId = "ProjectA" });

        Assert.True(result.Success);
        Assert.Equal("Raft majority commit reached", result.Message);
        Assert.Equal("ProjectA", result.ChannelId);
        Assert.Equal(1, replicator.CallCount);
        Assert.Equal(block.Hash, replicator.Context);

        var command = JsonSerializer.Deserialize<RaftBlockCommitCommand>(replicator.Payload);
        Assert.NotNull(command);
        Assert.Equal(block.Hash, command.Hash);
        Assert.Equal(proof.ProducerPublicKey, command.ContributionProof.ProducerPublicKey);
    }

    [Fact]
    public async Task SubmitAsync_ShouldReturnFailureWhenMajorityCommitIsNotReached()
    {
        var block = new Block
        {
            Index = 1,
            ChannelId = "ProjectA",
            Hash = "block-hash",
            PreviousHash = "previous-hash",
            ValidatorPublicKey = "producer-pk",
            Signature = "signature",
            TimestampUnixSeconds = 12345,
            Data = "{\"Type\":\"Update\"}"
        };
        var proof = new ContributionProof(
            "ProjectA",
            1,
            "producer-pk",
            10,
            "snapshot-hash",
            Array.Empty<string>());
        var submitter = new RaftBlockFinalitySubmitter(
            new CapturingRaftCommandReplicator(committed: false),
            Options.Create(new RaftOptions()),
            NullLogger<RaftBlockFinalitySubmitter>.Instance);

        var result = await submitter.SubmitAsync(
            new BlockProposal(block, proof, DateTime.UtcNow),
            new BlockModel { ChannelId = "ProjectA" });

        Assert.False(result.Success);
        Assert.Equal("Raft majority commit was not reached", result.Message);
        Assert.Equal("ProjectA", result.ChannelId);
    }

    [Fact]
    public async Task RaftCommittedBlockCommandApplier_ShouldApplyCommittedCommandToStorage()
    {
        bool previousProofOfWork = NetworkParameters.RequireProofOfWork;
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-apply-{Guid.NewGuid():N}.db");
        string walPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-apply-wal-{Guid.NewGuid():N}");
        string snapshotPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-apply-snapshots-{Guid.NewGuid():N}");

        try
        {
            NetworkParameters.RequireProofOfWork = false;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NodeDbPassword"] = "",
                    ["P2P:SyncToken"] = "test-sync-token",
                    ["P2P:RegistrationToken"] = "test-registration-token",
                    ["P2P:NodeId"] = "test-node",
                    ["P2P:PublicUrl"] = "http://localhost:5999",
                    ["P2P:IdentityKeyPath"] = Path.Combine(Path.GetTempPath(), $"nexus-node-key-{Guid.NewGuid():N}.p256.key"),
                    ["OraclePrivateKeyPassword"] = "test-oracle-password",
                    ["OraclePublicKey"] = "auto",
                    ["WebhookSecret"] = "test-webhook-secret",
                    ["Raft:SnapshotPath"] = snapshotPath,
                    ["Raft:Snapshot:EntryThreshold"] = "2"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
            services.AddLogging();
            services.AddSignalR();
            var database = new DatabaseManager(dbPath);
            services.AddNexusNodeServices(database);

            await using var provider = services.BuildServiceProvider();
            var manager = provider.GetRequiredService<BlockchainManager>();
            var latest = manager.GetLatestBlock("System");
            Assert.NotNull(latest);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            string data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"RaftApplyProj\",\"User\":\"Alice\"}";
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var block = new Block
            {
                Index = latest!.Index + 1,
                ChannelId = "System",
                PreviousHash = latest.Hash,
                TimestampUnixSeconds = timestamp,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime,
                Data = data,
                ValidatorPublicKey = publicKey
            };
            block.Signature = Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes($"{block.Index}{block.TimestampUnixSeconds}{block.Data}{block.PreviousHash}"),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            manager.MineBlock(block);

            var proof = new ContributionProof(
                "System",
                block.Index,
                publicKey,
                1,
                "snapshot-hash",
                Array.Empty<string>());
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                RaftBlockCommitCommand.FromProposal(new BlockProposal(block, proof, DateTime.UtcNow)));

            var stateMachine = provider.GetRequiredService<RaftBlockStateMachine>();
            await stateMachine.RestoreAsync();
            await using var wal = new WriteAheadLog(
                new WriteAheadLog.Options { Location = walPath },
                stateMachine);
            long raftLogIndex = await wal.AppendAsync(new BinaryLogEntry { Content = payload, Term = 7 });
            await wal.CommitAsync(raftLogIndex);
            await wal.WaitForApplyAsync(raftLogIndex);

            var stored = database.GetLatestBlock("System");
            Assert.NotNull(stored);
            Assert.Equal(block.Hash, stored!.Hash);
            Assert.Equal(block.Data, stored.Data);
            var metadata = database.GetFinalityMetadata(block.Hash);
            Assert.NotNull(metadata);
            Assert.Equal(ConsensusFinalityModes.Raft, metadata!.FinalityMode);
            Assert.Equal(raftLogIndex, metadata.RaftLogIndex);
            Assert.Equal(7, metadata.RaftTerm);
            Assert.Null(stateMachine.GetDiagnostics().CurrentSnapshotIndex);

            long duplicateIndex = await wal.AppendAsync(new BinaryLogEntry { Content = payload, Term = 7 });
            await wal.CommitAsync(duplicateIndex);
            await wal.WaitForApplyAsync(duplicateIndex);
            long checkpointIndex = await wal.AppendAsync(new BinaryLogEntry
            {
                Content = RaftBlockStateMachine.SnapshotCheckpointPayload,
                Term = 7
            });
            await wal.CommitAsync(checkpointIndex);
            await wal.WaitForApplyAsync(checkpointIndex);
            var diagnostics = stateMachine.GetDiagnostics();
            Assert.True(diagnostics.StateMachineHealthy, diagnostics.StateMachineFailure);
            Assert.Equal(checkpointIndex, diagnostics.LastAppliedIndex);
            Assert.Equal(duplicateIndex, diagnostics.CurrentSnapshotIndex);
            Assert.Equal(duplicateIndex, diagnostics.PublishedSnapshotIndex);
            Assert.True(File.Exists(Path.Combine(snapshotPath, $"{duplicateIndex}-7")));
        }
        finally
        {
            NetworkParameters.RequireProofOfWork = previousProofOfWork;
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
            TryDeleteDirectory(walPath);
            TryDeleteDirectory(snapshotPath);
        }
    }

    [Fact]
    public async Task RaftBlockStateMachine_ShouldFaultWithoutAdvancingAppliedIndexForRejectedCommittedCommand()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-reject-{Guid.NewGuid():N}.db");
        string walPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-reject-wal-{Guid.NewGuid():N}");
        string snapshotPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-reject-snapshots-{Guid.NewGuid():N}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NodeDbPassword"] = "",
                    ["NodeDatabase"] = dbPath,
                    ["Raft:SnapshotPath"] = snapshotPath,
                    ["Raft:Snapshot:Enabled"] = "false"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
            services.AddLogging();
            services.AddSignalR();
            services.AddNexusNodeServices(new DatabaseManager(dbPath));

            await using var provider = services.BuildServiceProvider();
            var stateMachine = provider.GetRequiredService<RaftBlockStateMachine>();
            await stateMachine.RestoreAsync();
            await using var wal = new WriteAheadLog(
                new WriteAheadLog.Options { Location = walPath },
                stateMachine);
            long raftLogIndex = await wal.AppendAsync(new BinaryLogEntry
            {
                Content = "{}"u8.ToArray(),
                Term = 9
            });

            await wal.CommitAsync(raftLogIndex);
            await Assert.ThrowsAnyAsync<Exception>(
                () => wal.WaitForApplyAsync(raftLogIndex).AsTask());

            var diagnostics = stateMachine.GetDiagnostics();
            Assert.False(diagnostics.StateMachineHealthy);
            Assert.Contains($"Log index {raftLogIndex}, term 9", diagnostics.StateMachineFailure);
            Assert.Equal(0, diagnostics.LastAppliedIndex);
            Assert.Equal(0, wal.LastAppliedIndex);
            Assert.Equal(raftLogIndex, wal.LastCommittedEntryIndex);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
            TryDeleteDirectory(walPath);
            TryDeleteDirectory(snapshotPath);
        }
    }

    private sealed class CapturingRaftCommandReplicator : IRaftCommandReplicator
    {
        private readonly bool _committed;

        public CapturingRaftCommandReplicator(bool committed)
        {
            _committed = committed;
        }

        public int CallCount { get; private set; }
        public byte[] Payload { get; private set; } = Array.Empty<byte>();
        public string Context { get; private set; } = string.Empty;

        public Task<bool> ReplicateAsync(ReadOnlyMemory<byte> payload, string context, CancellationToken token)
        {
            CallCount++;
            Payload = payload.ToArray();
            Context = context;
            return Task.FromResult(_committed);
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
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }
}

#pragma warning restore DOTNEXT001
