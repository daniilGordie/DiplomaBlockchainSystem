using Blockchain.Node.Services;
using Microsoft.Extensions.Configuration;

public sealed class NodeIdentityKeyLoadingTests
{
    [Fact]
    public void OracleIdentity_ShouldReportWrongPasswordClearly()
    {
        string keyPath = TempKeyPath("oracle-key");
        try
        {
            _ = new OracleIdentity(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OracleKeyPath"] = keyPath,
                    ["OraclePrivateKeyPassword"] = "correct-password"
                })
                .Build());

            var ex = Assert.Throws<InvalidOperationException>(() => new OracleIdentity(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OracleKeyPath"] = keyPath,
                    ["OraclePrivateKeyPassword"] = "wrong-password"
                })
                .Build()));

            Assert.Contains("Cannot load oracle key", ex.Message);
            Assert.Contains("OraclePrivateKeyPassword", ex.Message);
        }
        finally
        {
            TryDelete(keyPath);
        }
    }

    [Fact]
    public void ProducerIdentity_ShouldReportWrongPasswordClearly()
    {
        string keyPath = TempKeyPath("producer-key");
        try
        {
            _ = new ProducerIdentity(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Consensus:ProducerKeyPath"] = keyPath,
                    ["Consensus:ProducerPrivateKeyPassword"] = "correct-password"
                })
                .Build());

            var ex = Assert.Throws<InvalidOperationException>(() => new ProducerIdentity(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Consensus:ProducerKeyPath"] = keyPath,
                    ["Consensus:ProducerPrivateKeyPassword"] = "wrong-password"
                })
                .Build()));

            Assert.Contains("Cannot load producer key", ex.Message);
            Assert.Contains("Consensus:ProducerPrivateKeyPassword", ex.Message);
        }
        finally
        {
            TryDelete(keyPath);
        }
    }

    private static string TempKeyPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.dat");

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
            // Best-effort cleanup for temp key files.
        }
    }
}
