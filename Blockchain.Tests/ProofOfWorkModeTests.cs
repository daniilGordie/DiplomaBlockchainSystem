using Blockchain.Application.Analytics;
using Blockchain.Core;
using Blockchain.Core.Constants;
using Blockchain.Core.Contracts;
using Blockchain.Infrastructure.Services;

namespace Blockchain.Tests;

public sealed class ProofOfWorkModeTests
{
    [Fact]
    public void BlockMiner_ShouldNotSearchNonceWhenProofOfWorkIsDisabled()
    {
        bool previous = NetworkParameters.RequireProofOfWork;
        try
        {
            NetworkParameters.RequireProofOfWork = false;
            var block = CreateGenesisLikeBlock();

            new BlockMiner().Mine(block);

            Assert.Equal(0, block.Nonce);
            Assert.Equal(block.CalculateHash(), block.Hash);
        }
        finally
        {
            NetworkParameters.RequireProofOfWork = previous;
        }
    }

    [Fact]
    public void PeerBlockValidator_ShouldAcceptNonTargetHashWhenProofOfWorkIsDisabled()
    {
        bool previous = NetworkParameters.RequireProofOfWork;
        try
        {
            NetworkParameters.RequireProofOfWork = false;
            var block = CreateGenesisLikeBlock();
            block.Hash = block.CalculateHash();

            var validator = new PeerBlockValidator(new EmptyChainReader(), new EmptyStateReader());
            var result = validator.Validate(block);

            Assert.Equal(PeerBlockValidationStatus.Accepted, result.Status);
        }
        finally
        {
            NetworkParameters.RequireProofOfWork = previous;
        }
    }

    [Fact]
    public void CoreBlockAuditVerifier_ShouldTreatProofOfWorkAsValidWhenDisabled()
    {
        bool previous = NetworkParameters.RequireProofOfWork;
        try
        {
            NetworkParameters.RequireProofOfWork = false;
            var verifier = new CoreBlockAuditVerifier();
            var block = new BlockSnapshot(
                1,
                DateTime.UtcNow,
                "{}",
                "previous",
                "not-a-target-hash",
                "pk",
                "sig",
                0,
                "ProjectA");

            Assert.True(verifier.HasValidProofOfWork(block));
        }
        finally
        {
            NetworkParameters.RequireProofOfWork = previous;
        }
    }

    private static Block CreateGenesisLikeBlock()
    {
        var block = new Block
        {
            Index = 0,
            PreviousHash = "0",
            Data = "{\"Source\":\"System\",\"Message\":\"Nexus Genesis Block\"}",
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimestampUnixSeconds = new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            ChannelId = "System"
        };
        block.Hash = block.CalculateHash();
        return block;
    }

    private sealed class EmptyChainReader : IChainReader
    {
        public bool HasBlocks(string channelId = "System") => false;
        public Block? GetLatestBlock(string channelId = "System") => null;
        public bool BlockExists(string blockHash, string channelId = "System") => false;
        public List<string> GetKnownChannels() => new();
        public List<Block> LoadChain(string channelId = "System") => new();
        public List<Block> LoadLatestBlocks(string channelId = "System", int count = 100) => new();
    }

    private sealed class EmptyStateReader : ISmartContractStateReader
    {
        public string? GetUserPublicKey(string userName) => null;
        public string GetUserRole(string projectId, string userName) => "None";
        public int GetUserBalance(string userName) => 0;
        public bool IsProjectExists(string projectId) => false;
        public string? GetProposalCreator(string proposalId) => null;
    }
}
