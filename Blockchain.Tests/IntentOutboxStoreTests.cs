using Blockchain.Core.Consensus;
using Blockchain.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

public class IntentOutboxStoreTests
{
    [Fact]
    public void IntentOutbox_ShouldPersistStatusAndRetrySchedule()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus-intent-outbox-{Guid.NewGuid():N}.db");
        try
        {
            var database = new DatabaseManager(dbPath, "test-db-password");
            var intent = new SignedIntent(
                "intent-1",
                "nexus-test",
                "Alpha",
                "System",
                "CreateProject",
                "{\"Type\":\"CreateProject\",\"ProjectId\":\"Alpha\"}",
                "public-key",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "nonce",
                "signature",
                "correlation-1",
                1);
            var now = DateTime.UtcNow;
            database.SaveIntent(new IntentOutboxRecord(
                intent,
                "{\"index\":1}",
                IntentStatus.QueuedOffline,
                0,
                now,
                now,
                null,
                now.AddSeconds(-1),
                "offline",
                "bootstrap",
                null));

            var retryable = Assert.Single(database.LoadRetryableIntents(DateTime.UtcNow, 10));
            Assert.Equal("intent-1", retryable.Intent.IntentId);
            Assert.Equal(IntentStatus.QueuedOffline, retryable.Status);

            database.UpdateIntentStatus(
                "intent-1",
                IntentStatus.Committed,
                2,
                DateTime.UtcNow,
                DateTime.UtcNow,
                null,
                "",
                "bootstrap",
                "block-hash",
                7,
                "proposal-1");

            var committed = database.GetIntent("intent-1");
            Assert.NotNull(committed);
            Assert.Equal(IntentStatus.Committed, committed.Status);
            Assert.Equal("block-hash", committed.CommittedBlockHash);
            Assert.Equal(7, committed.CommittedBlockIndex);
            Assert.Equal("proposal-1", committed.ProposalId);
            Assert.Contains(database.LoadIntentHistory("intent-1"), item => item.Status == IntentStatus.Committed && item.CommittedBlockHash == "block-hash");
            Assert.Empty(database.LoadRetryableIntents(DateTime.UtcNow, 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(dbPath);
            DeleteIfExists(dbPath + "-wal");
            DeleteIfExists(dbPath + "-shm");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
