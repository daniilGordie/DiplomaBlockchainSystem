using System.Net.Http.Json;
using Blockchain.UI.Application.Clients;
using Blockchain.UI.Services;

namespace Blockchain.UI.Infrastructure.Http;

public sealed class ArtifactIntegrationClient : IArtifactIntegrationClient
{
    private readonly HttpClient _http;
    private readonly KeyService _keyService;

    public ArtifactIntegrationClient(HttpClient http, KeyService keyService)
    {
        _http = http;
        _keyService = keyService;
    }

    public async Task<ArtifactAnchorResult> AnchorAsync(ArtifactAnchorCommand command)
    {
        string actor = _keyService.UserName ?? "";
        string timestamp = DateTime.UtcNow.ToString("O");
        string signable = $"ARTIFACT_REGISTER:{command.ProjectId}:{command.FileHash}:{actor}:{timestamp}";

        var request = new ArtifactAnchorRequest
        {
            ProjectId = command.ProjectId,
            FileHash = command.FileHash,
            FileName = command.FileName,
            SizeBytes = command.SizeBytes,
            ContentType = string.IsNullOrWhiteSpace(command.ContentType) ? "application/octet-stream" : command.ContentType,
            User = actor,
            RegisteredBy = actor,
            VerificationMethod = "IPFS CID",
            Timestamp = timestamp,
            UserPublicKey = _keyService.PublicKey ?? "",
            UserSignature = _keyService.SignData(signable)
        };

        using var response = await _http.PostAsJsonAsync($"{command.NodeUrl}/api/integrations/artifacts/register", request);
        if (response.IsSuccessStatusCode)
        {
            return new ArtifactAnchorResult(true, "");
        }

        string details = await response.Content.ReadAsStringAsync();
        return new ArtifactAnchorResult(false, $"Artifact anchor failed ({(int)response.StatusCode}): {details}");
    }

    private sealed class ArtifactAnchorRequest
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
}
