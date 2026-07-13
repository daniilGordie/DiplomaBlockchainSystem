using Blockchain.Infrastructure.Persistence;

public sealed class DatabaseManagerStartupTests
{
    [Fact]
    public void Constructor_ShouldReportUnreadableDatabaseClearly()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus-invalid-db-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(dbPath, "this is not a sqlite database");

            var ex = Assert.Throws<InvalidOperationException>(() => new DatabaseManager(dbPath, "test-password"));

            Assert.Contains("cannot be opened", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NodeDbPassword", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("recreate the node data volume", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-shm");
            TryDelete(dbPath + "-wal");
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
            // Best-effort cleanup for temp database files.
        }
    }
}
