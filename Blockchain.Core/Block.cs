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

        public Block()
        {
            Data = string.Empty;
            PreviousHash = string.Empty;
            Hash = string.Empty;
            ValidatorPublicKey = string.Empty;
            Signature = string.Empty;
        }

        public Block(int index, string data, string previousHash)
        {
            Index = index;
            Timestamp = DateTime.UtcNow;
            Data = data;
            PreviousHash = previousHash;
            Hash = string.Empty;
            ValidatorPublicKey = string.Empty;
            Signature = string.Empty;
            Nonce = 0;
        }

        public string CalculateHash()
        {
            string rawData = $"{Index}{Timestamp:O}{Data}{PreviousHash}{ValidatorPublicKey}{Signature}{Nonce}";

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }

        public bool VerifySignature()
        {
            if (Index == 0 || ValidatorPublicKey == "GitHub-Oracle-Node" || ValidatorPublicKey == "System") return true;

            if (string.IsNullOrEmpty(ValidatorPublicKey) || string.IsNullOrEmpty(Signature))
                return false;

            try
            {
                using (ECDsa ecdsa = ECDsa.Create())
                {
                    ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ValidatorPublicKey), out _);

                    byte[] dataToVerify = Encoding.UTF8.GetBytes(Data);
                    byte[] signatureBytes = Convert.FromBase64String(Signature);

                    return ecdsa.VerifyData(dataToVerify, signatureBytes, HashAlgorithmName.SHA256);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}