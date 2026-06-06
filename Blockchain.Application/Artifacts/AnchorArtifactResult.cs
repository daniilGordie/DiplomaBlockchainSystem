namespace Blockchain.Application.Artifacts;

public enum AnchorArtifactStatus
{
    ReadyToAnchor,
    DuplicateIgnored,
    InvalidRequest,
    Unauthorized,
    Forbidden
}

public sealed record AnchorArtifactPayload(
    string Source,
    string Type,
    string FileName,
    string FileHash,
    long SizeBytes,
    string ContentType,
    string User,
    string ProjectId,
    string RegisteredBy,
    string VerificationMethod,
    string Timestamp);

public sealed record AnchorArtifactResult(
    AnchorArtifactStatus Status,
    string Message,
    string ProjectId = "",
    string FileHash = "",
    AnchorArtifactPayload? Payload = null)
{
    public bool ShouldAnchor => Status == AnchorArtifactStatus.ReadyToAnchor && Payload != null;
}
