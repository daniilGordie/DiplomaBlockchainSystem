using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blockchain.UI.Application.Clients;

internal sealed class ArtifactAnchorApiRequest
{
    public string ProjectId { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "";
    public string User { get; set; } = "";
    public string RegisteredBy { get; set; } = "";
    public string VerificationMethod { get; set; } = "IPFS CID";
    public string Timestamp { get; set; } = "";
    public string UserPublicKey { get; set; } = "";
    public string UserSignature { get; set; } = "";
}

internal sealed class GitRepositoryConnectApiRequest
{
    public string ProjectId { get; set; } = "";
    public string Repository { get; set; } = "";
    public string User { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string UserPublicKey { get; set; } = "";
    public string UserSignature { get; set; } = "";
}

internal sealed record NetworkStatusProbe(string NetworkId);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArtifactAnchorApiRequest))]
[JsonSerializable(typeof(GitRepositoryConnectApiRequest))]
[JsonSerializable(typeof(NetworkStatusProbe))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(JsonElement))]
internal partial class IntegrationApiJsonContext : JsonSerializerContext
{
    public static IntegrationApiJsonContext Indented { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
}
