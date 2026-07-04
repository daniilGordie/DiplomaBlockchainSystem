using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node;
using Blockchain.Node.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Blockchain.Tests;

public sealed class ProjectEventAnchorServiceTests
{
    [Fact]
    public async Task AnchorAsync_ShouldRejectTrustedOracleEventWhenPoCProducerDiffers()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus-oracle-poc-{Guid.NewGuid():N}.db");
        string oracleKeyPath = Path.Combine(Path.GetTempPath(), $"nexus-oracle-key-{Guid.NewGuid():N}.dat");

        try
        {
            var database = new DatabaseManager(dbPath);
            var manager = new BlockchainManager(database, database, database, database, new NoopReplayStoreFactory());
            using var aliceKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var alicePublicKey = Convert.ToBase64String(aliceKey.ExportSubjectPublicKeyInfo());

            database.SaveBlock(new Block
            {
                Index = 0,
                ChannelId = "ProjectA",
                PreviousHash = "0",
                Hash = "project-genesis",
                Data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"ProjectA\",\"User\":\"Alice\"}",
                ValidatorPublicKey = alicePublicKey,
                Signature = "seed-signature",
                Timestamp = DateTime.UtcNow,
                TimestampUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, "ProjectA");
            database.SaveBlock(new Block
            {
                Index = 1,
                ChannelId = "ProjectA",
                PreviousHash = "project-genesis",
                Hash = "seed-contribution",
                Data = "{\"Type\":\"CodeCommit\",\"ProjectId\":\"ProjectA\",\"User\":\"Alice\",\"CommitHash\":\"abc1234\"}",
                ValidatorPublicKey = alicePublicKey,
                Signature = "seed-signature",
                Timestamp = DateTime.UtcNow,
                TimestampUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, "ProjectA");

            var scoreService = new ContributionScoreService(database, database);
            var selector = new ProducerSelector();
            var proposalFactory = new BlockProposalFactory(scoreService, selector);
            var verifier = new VerifyBlockProposalUseCase(new PoCVerifier(scoreService, selector));
            var oracle = new OracleIdentity(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OraclePrivateKeyPassword"] = "test-oracle-password",
                    ["OracleKeyPath"] = oracleKeyPath
                })
                .Build());
            var service = new ProjectEventAnchorService(
                manager,
                oracle,
                new ProducerIdentity(new ConfigurationBuilder().Build()),
                Options.Create(new ConsensusOptions
                {
                    EnableProofOfContributionValidation = true,
                    FinalityMode = ConsensusFinalityModes.Raft
                }),
                proposalFactory,
                verifier,
                new NoopFinalitySubmitter(),
                null!);

            var result = await service.AnchorAsync(
                "ProjectA",
                new
                {
                    Type = "CodeCommit",
                    ProjectId = "ProjectA",
                    User = "Alice",
                    CommitHash = "def5678",
                    Timestamp = DateTimeOffset.UtcNow.ToString("O")
                },
                DateTimeOffset.UtcNow.ToString("O"));

            Assert.False(result.Accepted);
            Assert.Equal("ProjectA", result.ChannelId);
            Assert.StartsWith("poc_producer_key_unavailable", result.Error);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
            TryDelete(oracleKeyPath);
        }
    }

    private sealed class NoopFinalitySubmitter : IBlockFinalitySubmitter
    {
        public Task<BlockWriteResult> SubmitAsync(BlockProposal proposal, BlockModel sourceModel)
        {
            return Task.FromResult(new BlockWriteResult(true, "noop", proposal.Block.ChannelId));
        }
    }

    private sealed class NoopReplayStoreFactory : IReplayStoreFactory
    {
        public IBlockchainStore CreateReplayStore() => throw new NotSupportedException();
        public void CleanupReplayStore(IBlockchainStore replayStore) { }
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
}
