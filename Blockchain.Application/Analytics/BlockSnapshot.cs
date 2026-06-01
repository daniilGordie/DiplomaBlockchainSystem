namespace Blockchain.Application.Analytics;

public sealed record BlockSnapshot(
    int Index,
    DateTime Timestamp,
    string Data,
    string PreviousHash,
    string Hash,
    string ValidatorPublicKey,
    string Signature,
    long Nonce,
    string ChannelId);
