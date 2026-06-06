namespace Blockchain.Application.Artifacts;

public sealed record AnchorArtifactCommand(
    string ProjectId,
    string FileHash,
    string FileName,
    long SizeBytes,
    string ContentType,
    string User,
    string RegisteredBy,
    string VerificationMethod,
    string Timestamp,
    string UserPublicKey,
    string UserSignature);
