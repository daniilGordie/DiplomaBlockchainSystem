using System;
using System.Security.Cryptography;
using System.Text;

namespace Blockchain.Core
{
    public class Block
    {
        public int Index { get; set; }
        public DateTime Timestamp { get; set; }
        public string Data { get; set; }
        public string PreviousHash { get; set; }
        public string Hash { get; set; }
        public string ValidatorPublicKey { get; set; }
        public string Signature { get; set; }
        public long Nonce { get; set; }
        public string ChannelId { get; set; } = "System";
        public long TimestampUnixSeconds { get; set; }

        public Block()
        {
            Data = string.Empty;
            PreviousHash = string.Empty;
            Hash = string.Empty;
            ValidatorPublicKey = string.Empty;
            Signature = string.Empty;
        }

        private string GetSignableData()
        {
            if (TimestampUnixSeconds > 0)
            {
                return $"{Index}{TimestampUnixSeconds}{Data}{PreviousHash}";
            }

            return $"{Index}{Timestamp:O}{Data}{PreviousHash}";
        }

        public string CalculateHash()
        {
            string timestampComponent = TimestampUnixSeconds > 0
                ? TimestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Timestamp.ToString("s");
            string rawData = $"{Index}{timestampComponent}{Data}{PreviousHash}{ValidatorPublicKey}{Signature}{Nonce}";

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawData));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public bool VerifySignature()
        {
            if (IsSystemGenesisBlock()) return true;

            if (string.IsNullOrEmpty(ValidatorPublicKey) || string.IsNullOrEmpty(Signature))
                return false;

            try
            {
                byte[] signatureBytes = Convert.FromBase64String(Signature);

                if (signatureBytes.Length != 64)
                {
                    Console.WriteLine($"[Cryptography] Rejected: Invalid raw ECDSA signature length ({signatureBytes.Length} bytes).");
                    return false;
                }

                using (ECDsa ecdsa = ECDsa.Create())
                {
                    ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ValidatorPublicKey), out _);

                    byte[] dataToVerify = Encoding.UTF8.GetBytes(GetSignableData());

                    return ecdsa.VerifyData(
                        dataToVerify,
                        signatureBytes,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cryptography] Critical signature verification error: {ex.Message}");
                return false;
            }
        }

        public bool IsSystemGenesisBlock()
        {
            return ChannelId == "System"
                && Index == 0
                && PreviousHash == "0"
                && Data == "{\"Source\":\"System\",\"Message\":\"Nexus Genesis Block\"}";
        }
    }
}
