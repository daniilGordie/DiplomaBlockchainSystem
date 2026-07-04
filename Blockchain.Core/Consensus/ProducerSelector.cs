namespace Blockchain.Core.Consensus;

public sealed class ProducerSelector
{
    public ProducerSelection? Select(ContributionSnapshot snapshot)
    {
        var selected = snapshot.Scores
            .Where(score => score.Score > 0)
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.PublicKey, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected == null)
        {
            return null;
        }

        return new ProducerSelection(
            snapshot.ProjectId,
            snapshot.Epoch,
            selected.PublicKey,
            selected.Score,
            snapshot.SnapshotHash,
            selected.EvidenceBlockHashes);
    }
}
