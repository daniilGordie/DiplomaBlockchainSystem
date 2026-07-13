using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blockchain.Core;
using Blockchain.Core.Contracts;

namespace Blockchain.Core.Consensus;

public sealed class ContributionScoreService
{
    private readonly IChainReader _chainReader;
    private readonly ISmartContractStateReader _stateReader;

    public ContributionScoreService(
        IChainReader chainReader,
        ISmartContractStateReader stateReader)
    {
        _chainReader = chainReader;
        _stateReader = stateReader;
    }

    public ContributionSnapshot BuildSnapshot(string projectId, long epoch)
    {
        string safeProjectId = ChannelName.Normalize(projectId);
        var scores = new Dictionary<string, ScoreAccumulator>(StringComparer.Ordinal);

        foreach (var block in LoadContributionEvidenceBlocks(safeProjectId))
        {
            if (string.IsNullOrWhiteSpace(block.Data))
            {
                continue;
            }

            if (!TryReadContribution(block, safeProjectId, out var contribution))
            {
                continue;
            }

            string? publicKey = _stateReader.GetUserPublicKey(contribution.UserName);
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                continue;
            }

            if (!scores.TryGetValue(publicKey, out var accumulator))
            {
                accumulator = new ScoreAccumulator(contribution.UserName, publicKey);
                scores[publicKey] = accumulator;
            }

            accumulator.Add(contribution.Points, block.Hash);
        }

        var orderedScores = scores.Values
            .Where(score => score.Score > 0)
            .Select(score => score.ToContributionScore())
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.PublicKey, StringComparer.Ordinal)
            .ToArray();

        string snapshotHash = ComputeSnapshotHash(safeProjectId, epoch, orderedScores);
        return new ContributionSnapshot(safeProjectId, epoch, orderedScores, snapshotHash);
    }

    private IEnumerable<Block> LoadContributionEvidenceBlocks(string safeProjectId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in _chainReader.LoadChain(safeProjectId).OrderBy(block => block.Index))
        {
            if (seen.Add(GetEvidenceIdentity(safeProjectId, block)))
            {
                yield return block;
            }
        }

        if (!string.Equals(safeProjectId, "System", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var block in _chainReader.LoadChain("System").OrderBy(block => block.Index))
            {
                if (seen.Add(GetEvidenceIdentity("System", block)))
                {
                    yield return block;
                }
            }
        }
    }

    private static string GetEvidenceIdentity(string channelId, Block block) =>
        !string.IsNullOrWhiteSpace(block.Hash)
            ? block.Hash
            : $"{channelId}:{block.Index}:{block.TimestampUnixSeconds}:{block.PreviousHash}";

    private static bool TryReadContribution(Block block, string projectId, out ContributionEvent contribution)
    {
        contribution = default;

        try
        {
            using var doc = JsonDocument.Parse(block.Data);
            var root = doc.RootElement;
            string payloadProjectId = root.TryGetProperty("ProjectId", out var projectProp)
                ? projectProp.GetString() ?? string.Empty
                : string.Empty;

            if (!string.Equals(ChannelName.Normalize(payloadProjectId), ChannelName.Normalize(projectId), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string type = root.TryGetProperty("Type", out var typeProp)
                ? typeProp.GetString() ?? string.Empty
                : string.Empty;
            string user = root.TryGetProperty("User", out var userProp)
                ? userProp.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(user))
            {
                return false;
            }

            int points = GetPoints(type, root);
            if (points <= 0)
            {
                return false;
            }

            contribution = new ContributionEvent(user.Trim(), points);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetPoints(string type, JsonElement root)
    {
        if (type.Equals("CodeCommit", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (type.Equals("CreateProject", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (type.Equals("Register", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (type.Equals("CreateDocument", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("UpdateDocument", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (type.Equals("CreateProposal", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("CastVote", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("AssignRole", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (type.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("Update", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("Move", StringComparison.OrdinalIgnoreCase))
        {
            int status = root.TryGetProperty("Status", out var statusProp) && statusProp.TryGetInt32(out var value)
                ? value
                : 0;

            return status == 2 ? 4 : 1;
        }

        return 0;
    }

    private static string ComputeSnapshotHash(
        string projectId,
        long epoch,
        IReadOnlyList<ContributionScore> scores)
    {
        var builder = new StringBuilder();
        builder.Append(projectId).Append('|').Append(epoch);

        foreach (var score in scores)
        {
            builder.Append('|')
                .Append(score.PublicKey)
                .Append(':')
                .Append(score.UserName)
                .Append(':')
                .Append(score.Score)
                .Append(':')
                .Append(string.Join(',', score.EvidenceBlockHashes.OrderBy(hash => hash, StringComparer.Ordinal)));
        }

        using var sha256 = SHA256.Create();
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private readonly record struct ContributionEvent(string UserName, int Points);

    private sealed class ScoreAccumulator
    {
        private readonly SortedSet<string> _evidenceBlockHashes = new(StringComparer.Ordinal);

        public ScoreAccumulator(string userName, string publicKey)
        {
            UserName = userName;
            PublicKey = publicKey;
        }

        public string UserName { get; }
        public string PublicKey { get; }
        public int Score { get; private set; }

        public void Add(int points, string blockHash)
        {
            Score += points;
            if (!string.IsNullOrWhiteSpace(blockHash))
            {
                _evidenceBlockHashes.Add(blockHash);
            }
        }

        public ContributionScore ToContributionScore() =>
            new(UserName, PublicKey, Score, _evidenceBlockHashes.ToArray());
    }
}
