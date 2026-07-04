using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;

namespace Blockchain.Tests;

public sealed class DatabaseManagerEncryptionTests
{
    [Fact]
    public void DatabaseManager_ShouldReadEncryptedFieldsAfterDatabasePathChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nexus-db-move-{Guid.NewGuid():N}");
        string sourceDir = Path.Combine(root, "host-seed");
        string movedDir = Path.Combine(root, "container-data");
        string sourcePath = Path.Combine(sourceDir, "node.db");
        string movedPath = Path.Combine(movedDir, "node.db");
        const string dbPassword = "portable-encryption-password";
        const string userPublicKey = "alice-public-key";

        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(movedDir);

            var sourceDatabase = new DatabaseManager(sourcePath, dbPassword);
            sourceDatabase.SaveUserPublicKey("Alice", userPublicKey);
            sourceDatabase.SaveBlock(new Block
            {
                Index = 1,
                ChannelId = "ProjectA",
                Data = "{\"Type\":\"CodeCommit\",\"ProjectId\":\"ProjectA\",\"User\":\"Alice\"}",
                PreviousHash = "genesis",
                Hash = "commit-hash",
                ValidatorPublicKey = userPublicKey,
                Signature = "signature",
                Nonce = 0,
                Timestamp = DateTime.UtcNow,
                TimestampUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, "ProjectA");

            CopySqliteFiles(sourcePath, movedPath);

            var movedDatabase = new DatabaseManager(movedPath, dbPassword);
            var movedChain = movedDatabase.LoadChain("ProjectA");

            Assert.Equal(userPublicKey, movedDatabase.GetUserPublicKey("Alice"));
            Assert.Single(movedChain);
            Assert.Contains("\"CodeCommit\"", movedChain[0].Data);
            Assert.Equal(userPublicKey, movedChain[0].ValidatorPublicKey);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CopySqliteFiles(string sourcePath, string movedPath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string source = sourcePath + suffix;
            if (!File.Exists(source))
            {
                continue;
            }

            File.Copy(source, movedPath + suffix, overwrite: true);
        }
    }
}
