namespace Blockchain.Core.Constants
{
    public static class NetworkParameters
    {
        public static int TargetDifficulty { get; set; } = 3;
        public static string TargetPrefix => new string('0', TargetDifficulty);

        public static bool IsGenesisModeEnabled { get; set; } = true;
        public static int MinimumReputationThreshold { get; set; } = 10;
        public static decimal ReputationDecayLambda { get; set; } = 0.05m;

        public static string TrustedOraclePublicKey { get; set; } = string.Empty;
    }
}