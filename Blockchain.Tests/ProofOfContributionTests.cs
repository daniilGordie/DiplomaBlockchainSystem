using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Core.Contracts;

namespace Blockchain.Tests;

public sealed class ProofOfContributionTests
{
    [Fact]
    public void ProducerSelector_ShouldChooseHighestScoreDeterministically()
    {
        var chain = new FakeChainReader(new[]
        {
            BlockFor(0, "ProjectA", "h0", "0", "{\"Source\":\"System\",\"Message\":\"Genesis\"}"),
            BlockFor(1, "ProjectA", "h1", "h0", "{\"Type\":\"CodeCommit\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\",\"CommitHash\":\"abc1234\"}"),
            BlockFor(2, "ProjectA", "h2", "h1", "{\"Type\":\"UpdateDocument\",\"ProjectId\":\"ProjectA\",\"User\":\"bob\",\"DocumentId\":\"D1\"}"),
            BlockFor(3, "ProjectA", "h3", "h2", "{\"Type\":\"Move\",\"ProjectId\":\"ProjectA\",\"User\":\"bob\",\"Status\":2}")
        });
        var state = new FakeStateReader(new Dictionary<string, string>
        {
            ["alice"] = "alice-pk",
            ["bob"] = "bob-pk"
        });

        var snapshot = new ContributionScoreService(chain, state).BuildSnapshot("ProjectA", epoch: 7);
        var selected = new ProducerSelector().Select(snapshot);

        Assert.NotNull(selected);
        Assert.Equal("bob-pk", selected.ProducerPublicKey);
        Assert.Equal(6, selected.ProducerScore);
        Assert.Equal(new[] { "h2", "h3" }, selected.EvidenceBlockHashes);
    }

    [Fact]
    public void ProducerSelector_ShouldUsePublicKeyTieBreak()
    {
        var snapshot = new ContributionSnapshot(
            "ProjectA",
            1,
            new[]
            {
                new ContributionScore("alice", "z-pk", 5, new[] { "h1" }),
                new ContributionScore("bob", "a-pk", 5, new[] { "h2" })
            },
            "snapshot");

        var selected = new ProducerSelector().Select(snapshot);

        Assert.NotNull(selected);
        Assert.Equal("a-pk", selected.ProducerPublicKey);
    }

    [Fact]
    public void PoCVerifier_ShouldAcceptSelectedProducerProof()
    {
        var chain = new FakeChainReader(new[]
        {
            BlockFor(0, "ProjectA", "h0", "0", "{}"),
            BlockFor(1, "ProjectA", "h1", "h0", "{\"Type\":\"CodeCommit\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\",\"CommitHash\":\"abc1234\"}")
        });
        var state = new FakeStateReader(new Dictionary<string, string>
        {
            ["alice"] = "alice-pk"
        });
        var scoreService = new ContributionScoreService(chain, state);
        var snapshot = scoreService.BuildSnapshot("ProjectA", 10);
        var selected = new ProducerSelector().Select(snapshot)!;
        var proof = new ContributionProof(
            selected.ProjectId,
            selected.Epoch,
            selected.ProducerPublicKey,
            selected.ProducerScore,
            selected.ScoreSnapshotHash,
            selected.EvidenceBlockHashes);
        var proposal = new BlockProposal(
            BlockFor(2, "ProjectA", "h2", "h1", "{\"Type\":\"Update\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\"}", "alice-pk"),
            proof,
            DateTime.UtcNow);

        var result = new PoCVerifier(scoreService, new ProducerSelector()).Verify(proposal);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void BlockProposalFactory_ShouldBuildImplicitProofForSelectedProducer()
    {
        var chain = new FakeChainReader(new[]
        {
            BlockFor(0, "ProjectA", "h0", "0", "{}"),
            BlockFor(1, "ProjectA", "h1", "h0", "{\"Type\":\"CodeCommit\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\",\"CommitHash\":\"abc1234\"}")
        });
        var state = new FakeStateReader(new Dictionary<string, string>
        {
            ["alice"] = "alice-pk"
        });
        var scoreService = new ContributionScoreService(chain, state);
        var factory = new BlockProposalFactory(scoreService, new ProducerSelector());
        var block = BlockFor(2, "ProjectA", "h2", "h1", "{\"Type\":\"Update\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\"}", "alice-pk");

        var result = factory.BuildImplicitProposal(block);

        Assert.True(result.Accepted, result.Reason);
        Assert.NotNull(result.Proposal);
        Assert.Equal("alice-pk", result.Proposal.ContributionProof.ProducerPublicKey);
        Assert.Equal(2, result.Proposal.ContributionProof.Epoch);
    }

    [Fact]
    public void BlockProposalFactory_ShouldRejectWhenNoConfirmedContributorsExist()
    {
        var chain = new FakeChainReader(new[]
        {
            BlockFor(0, "ProjectA", "h0", "0", "{}")
        });
        var state = new FakeStateReader(new Dictionary<string, string>());
        var scoreService = new ContributionScoreService(chain, state);
        var factory = new BlockProposalFactory(scoreService, new ProducerSelector());
        var block = BlockFor(1, "ProjectA", "h1", "h0", "{\"Type\":\"Update\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\"}", "alice-pk");

        var result = factory.BuildImplicitProposal(block);

        Assert.False(result.Accepted);
        Assert.Equal("no eligible producer", result.Reason);
    }

    [Fact]
    public void BlockProposalFactory_ShouldAllowBootstrapProjectCreationWithoutPriorContributors()
    {
        var chain = new FakeChainReader(Array.Empty<Block>());
        var state = new FakeStateReader(new Dictionary<string, string>());
        var scoreService = new ContributionScoreService(chain, state);
        var factory = new BlockProposalFactory(scoreService, new ProducerSelector());
        var block = BlockFor(
            0,
            "System",
            "h0",
            "0",
            "{\"Type\":\"CreateProject\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\"}",
            "alice-pk");

        var result = factory.BuildImplicitProposal(block);

        Assert.True(result.Accepted, result.Reason);
        Assert.NotNull(result.Proposal);
        Assert.Equal("alice-pk", result.Proposal.ContributionProof.ProducerPublicKey);
        Assert.Equal(0, result.Proposal.ContributionProof.ProducerScore);
        Assert.Empty(result.Proposal.ContributionProof.EvidenceBlockHashes);
    }

    [Fact]
    public void PoCVerifier_ShouldAcceptBootstrapProjectCreationProof()
    {
        var chain = new FakeChainReader(Array.Empty<Block>());
        var state = new FakeStateReader(new Dictionary<string, string>());
        var scoreService = new ContributionScoreService(chain, state);
        var factory = new BlockProposalFactory(scoreService, new ProducerSelector());
        var block = BlockFor(
            0,
            "System",
            "h0",
            "0",
            "{\"Type\":\"CreateProject\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\"}",
            "alice-pk");
        var proposal = factory.BuildImplicitProposal(block).Proposal!;

        var result = new PoCVerifier(scoreService, new ProducerSelector()).Verify(proposal);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void ContributionScoreService_ShouldUseSystemProjectCreationAsInitialProjectContribution()
    {
        var chain = new FakeChainReader(new[]
        {
            BlockFor(0, "System", "h0", "0", "{\"Type\":\"CreateProject\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\"}", "alice-pk")
        });
        var state = new FakeStateReader(new Dictionary<string, string>
        {
            ["alice"] = "alice-pk"
        });
        var scoreService = new ContributionScoreService(chain, state);

        var snapshot = scoreService.BuildSnapshot("ProjectA", epoch: 1);
        var selected = new ProducerSelector().Select(snapshot);

        Assert.NotNull(selected);
        Assert.Equal("alice-pk", selected.ProducerPublicKey);
        Assert.Equal(3, selected.ProducerScore);
        Assert.Equal(new[] { "h0" }, selected.EvidenceBlockHashes);
    }

    [Fact]
    public void PoCVerifier_ShouldRejectBlockSignedByNonSelectedProducer()
    {
        var chain = new FakeChainReader(new[]
        {
            BlockFor(0, "ProjectA", "h0", "0", "{}"),
            BlockFor(1, "ProjectA", "h1", "h0", "{\"Type\":\"CodeCommit\",\"ProjectId\":\"ProjectA\",\"User\":\"alice\",\"CommitHash\":\"abc1234\"}")
        });
        var state = new FakeStateReader(new Dictionary<string, string>
        {
            ["alice"] = "alice-pk",
            ["bob"] = "bob-pk"
        });
        var scoreService = new ContributionScoreService(chain, state);
        var snapshot = scoreService.BuildSnapshot("ProjectA", 10);
        var selected = new ProducerSelector().Select(snapshot)!;
        var proof = new ContributionProof(
            selected.ProjectId,
            selected.Epoch,
            selected.ProducerPublicKey,
            selected.ProducerScore,
            selected.ScoreSnapshotHash,
            selected.EvidenceBlockHashes);
        var proposal = new BlockProposal(
            BlockFor(2, "ProjectA", "h2", "h1", "{\"Type\":\"Update\",\"ProjectId\":\"ProjectA\",\"User\":\"bob\"}", "bob-pk"),
            proof,
            DateTime.UtcNow);

        var result = new PoCVerifier(scoreService, new ProducerSelector()).Verify(proposal);

        Assert.False(result.Accepted);
        Assert.Equal("block validator is not the selected producer", result.Reason);
    }

    private static Block BlockFor(
        int index,
        string channelId,
        string hash,
        string previousHash,
        string data,
        string validatorPublicKey = "")
    {
        return new Block
        {
            Index = index,
            ChannelId = channelId,
            Hash = hash,
            PreviousHash = previousHash,
            Data = data,
            ValidatorPublicKey = validatorPublicKey,
            Timestamp = DateTime.UnixEpoch.AddSeconds(index),
            TimestampUnixSeconds = index
        };
    }

    private sealed class FakeChainReader : IChainReader
    {
        private readonly List<Block> _blocks;

        public FakeChainReader(IEnumerable<Block> blocks)
        {
            _blocks = blocks.ToList();
        }

        public bool HasBlocks(string channelId = "System") => _blocks.Any(block => block.ChannelId == channelId);

        public Block? GetLatestBlock(string channelId = "System") =>
            _blocks.Where(block => block.ChannelId == channelId).OrderByDescending(block => block.Index).FirstOrDefault();

        public bool BlockExists(string blockHash, string channelId = "System") =>
            _blocks.Any(block => block.ChannelId == channelId && block.Hash == blockHash);

        public List<string> GetKnownChannels() =>
            _blocks.Select(block => block.ChannelId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public List<Block> LoadChain(string channelId = "System") =>
            _blocks.Where(block => block.ChannelId == channelId).OrderBy(block => block.Index).ToList();

        public List<Block> LoadLatestBlocks(string channelId = "System", int count = 100) =>
            LoadChain(channelId).OrderByDescending(block => block.Index).Take(count).ToList();
    }

    private sealed class FakeStateReader : ISmartContractStateReader
    {
        private readonly IReadOnlyDictionary<string, string> _publicKeys;

        public FakeStateReader(IReadOnlyDictionary<string, string> publicKeys)
        {
            _publicKeys = publicKeys;
        }

        public string? GetUserPublicKey(string userName) =>
            _publicKeys.TryGetValue(userName, out var publicKey) ? publicKey : null;

        public string GetUserRole(string projectId, string userName) => "Developer";

        public int GetUserBalance(string userName) => 0;

        public bool IsProjectExists(string projectId) => true;

        public string? GetProposalCreator(string proposalId) => null;
    }
}
