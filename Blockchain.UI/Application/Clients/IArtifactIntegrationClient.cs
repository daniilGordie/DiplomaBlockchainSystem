namespace Blockchain.UI.Application.Clients;

public interface IArtifactIntegrationClient
{
    Task<ArtifactAnchorResult> AnchorAsync(ArtifactAnchorCommand command);
}

public sealed record ArtifactAnchorCommand(
    string NodeUrl,
    string ProjectId,
    string FileHash,
    string FileName,
    long SizeBytes,
    string ContentType);

public sealed record ArtifactAnchorResult(
    bool Success,
    string Message);
