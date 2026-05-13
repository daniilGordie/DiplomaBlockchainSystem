using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Blockchain.Node.Services
{
    public static class GitWebhookSecurity
    {
        private static readonly Regex CommitHashPattern = new("^[a-fA-F0-9]+$", RegexOptions.Compiled);
        private static readonly Regex ProjectIdPattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
        private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.\\-/:]+$", RegexOptions.Compiled);

        public static string ComputeHmacSha256(string payload, string secret)
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

            using var hmac = new HMACSHA256(secretBytes);
            byte[] hashBytes = hmac.ComputeHash(payloadBytes);

            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        public static bool IsValidSignature(string payload, string secret, string suppliedHeader)
        {
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(suppliedHeader))
            {
                return false;
            }

            string expected = "sha256=" + ComputeHmacSha256(payload, secret);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected.ToLowerInvariant());
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(suppliedHeader.ToLowerInvariant());

            return expectedBytes.Length == suppliedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        public static bool IsValidCommitHash(string commitHash)
        {
            return !string.IsNullOrWhiteSpace(commitHash)
                && commitHash.Length >= 7
                && commitHash.Length <= 64
                && CommitHashPattern.IsMatch(commitHash);
        }

        public static bool IsValidProjectId(string projectId)
        {
            return !string.IsNullOrWhiteSpace(projectId)
                && ProjectIdPattern.IsMatch(projectId);
        }

        public static bool IsValidRepository(string repository)
        {
            return !string.IsNullOrWhiteSpace(repository)
                && repository.Length <= 200
                && RepositoryPattern.IsMatch(repository);
        }
    }
}
