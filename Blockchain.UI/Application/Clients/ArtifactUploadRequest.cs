namespace Blockchain.UI.Application.Clients;

public sealed record ArtifactUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    string Base64Content);
