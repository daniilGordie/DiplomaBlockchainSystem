namespace Blockchain.Core.Constants
{
    public static class ConsensusRules
    {
        public const int TargetDifficulty = 3;

        public static readonly string TargetPrefix = new string('0', TargetDifficulty);
    }
}