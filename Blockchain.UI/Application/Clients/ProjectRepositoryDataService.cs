using System.Text.Json;
using Blockchain.Node;
using Blockchain.UI.Application.Security;
using Blockchain.UI.Models;
using Blockchain.UI.Services;

namespace Blockchain.UI.Application.Clients;

public sealed class ProjectRepositoryDataService : IProjectRepositoryDataService
{
    private readonly BlockchainService.BlockchainServiceClient _blockchainClient;
    private readonly IReadRequestAuthorizer _readAuthorizer;
    private readonly KeyService _keyService;

    public ProjectRepositoryDataService(
        BlockchainService.BlockchainServiceClient blockchainClient,
        IReadRequestAuthorizer readAuthorizer,
        KeyService keyService)
    {
        _blockchainClient = blockchainClient;
        _readAuthorizer = readAuthorizer;
        _keyService = keyService;
    }

    public async Task<ProjectRepositoryData> LoadAsync(string projectId)
    {
        var request = new ChainRequest
        {
            ChannelId = projectId,
            Count = 100,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? "",
            AfterIndex = -1
        };
        _readAuthorizer.Apply(request, $"CHAIN:{projectId}");

        var response = await _blockchainClient.GetChainAsync(request);
        var commits = new List<CommitPayloadUI>();
        var artifacts = new List<ArtifactPayloadUI>();

        foreach (var block in response.Blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Data))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(block.Data);
                var root = doc.RootElement;

                string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                if (type == "CodeCommit")
                {
                    commits.Add(new CommitPayloadUI
                    {
                        CommitHash = root.TryGetProperty("CommitHash", out var hash) ? hash.GetString() ?? "" : "",
                        Message = root.TryGetProperty("Message", out var msg) ? msg.GetString() ?? "" : "",
                        Author = root.TryGetProperty("User", out var user) ? user.GetString() ?? "Unknown" : "Unknown",
                        PatchCid = root.TryGetProperty("PatchCid", out var patch) ? patch.GetString() ?? "" : "",
                        Repository = root.TryGetProperty("Repository", out var repo) ? repo.GetString() ?? "" : "",
                        Provider = root.TryGetProperty("Provider", out var provider) ? provider.GetString() ?? "" : "",
                        Branch = root.TryGetProperty("Branch", out var branch) ? branch.GetString() ?? "" : ""
                    });
                }
                else if (TryParseArtifact(root, out var artifact))
                {
                    artifacts.Add(artifact);
                }
            }
            catch
            {
                // Skip malformed historical payloads to keep the repository view available.
            }
        }

        commits.Reverse();
        return new ProjectRepositoryData(commits, artifacts);
    }

    private static bool TryParseArtifact(JsonElement root, out ArtifactPayloadUI artifact)
    {
        artifact = new ArtifactPayloadUI();

        string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
        string source = root.TryGetProperty("Source", out var sourceProp) ? sourceProp.GetString() ?? "" : "";

        bool isArtifact = type.Equals("Register", StringComparison.OrdinalIgnoreCase)
            && (source.Equals("ArtifactRegistry", StringComparison.OrdinalIgnoreCase)
                || source.Equals("UserArtifact", StringComparison.OrdinalIgnoreCase)
                || root.TryGetProperty("FileHash", out _));

        if (!isArtifact)
        {
            return false;
        }

        artifact = new ArtifactPayloadUI
        {
            FileName = root.TryGetProperty("FileName", out var fileName) ? fileName.GetString() ?? "artifact.bin" : "artifact.bin",
            FileHash = root.TryGetProperty("FileHash", out var fileHash) ? fileHash.GetString() ?? "" : "",
            RegisteredBy = root.TryGetProperty("RegisteredBy", out var registeredBy)
                ? registeredBy.GetString() ?? ""
                : (root.TryGetProperty("User", out var user) ? user.GetString() ?? "" : ""),
            VerificationMethod = root.TryGetProperty("VerificationMethod", out var method) ? method.GetString() ?? "IPFS" : "IPFS",
            SizeBytes = root.TryGetProperty("SizeBytes", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : 0,
            ContentType = root.TryGetProperty("ContentType", out var contentType) ? contentType.GetString() ?? "" : "",
            Timestamp = root.TryGetProperty("Timestamp", out var timestamp) ? timestamp.GetString() ?? "" : ""
        };

        return !string.IsNullOrWhiteSpace(artifact.FileHash);
    }
}
