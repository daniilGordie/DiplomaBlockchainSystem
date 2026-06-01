using System.Security.Cryptography;
using System.Text;
using Blockchain.Core;
using Blockchain.Core.Constants;
using Blockchain.Infrastructure.Persistence;

public class BlockPersistenceTests
{
    [Fact]
    public void LoadChain_ShouldPreserveTimestampForHashAndSignatureValidation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexus-blocks-{Guid.NewGuid():N}.db");

        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var timestamp = DateTime.SpecifyKind(
                new DateTime(2026, 5, 24, 12, 30, 45, 123),
                DateTimeKind.Utc);

            var block = new Block
            {
                Index = 0,
                Timestamp = timestamp,
                Data = "roundtrip-payload",
                PreviousHash = "0",
                ValidatorPublicKey = publicKey,
                ChannelId = "AuditRoundtrip"
            };

            string signableData = $"{block.Index}{block.Timestamp:O}{block.Data}{block.PreviousHash}";
            block.Signature = Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(signableData),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

            do
            {
                block.Nonce++;
                block.Hash = block.CalculateHash();
            }
            while (!block.Hash.StartsWith(NetworkParameters.TargetPrefix, StringComparison.Ordinal));

            var db = new DatabaseManager(dbPath, "");
            db.SaveBlock(block, block.ChannelId);

            var loaded = Assert.Single(db.LoadChain(block.ChannelId));

            Assert.Equal(block.Timestamp.ToString("O"), loaded.Timestamp.ToString("O"));
            Assert.Equal(block.Hash, loaded.CalculateHash());
            Assert.True(loaded.VerifySignature());
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
