using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.Node;
using Blockchain.UI.Application.Clients;

namespace Blockchain.UI.Infrastructure.Http;

public sealed class ConsensusClient : IConsensusClient
{
    private readonly HttpClient _http;

    public ConsensusClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ConsensusProducerInfo> GetProducerInfoAsync(string nodeUrl, string projectId)
    {
        try
        {
            string safeProjectId = Uri.EscapeDataString(projectId);
            using var doc = await _http.GetFromJsonAsync(
                $"{nodeUrl}/api/consensus/projects/{safeProjectId}/producer",
                IntegrationApiJsonContext.Default.JsonDocument);
            if (doc == null)
            {
                return new ConsensusProducerInfo(null);
            }

            var root = doc.RootElement;
            bool hasEligibleProducer = ReadBool(root, "hasEligibleProducer", "HasEligibleProducer");
            if (!hasEligibleProducer)
            {
                return new ConsensusProducerInfo(null);
            }

            if (!TryGetProperty(root, "contributionProof", "ContributionProof", out var proofElement) ||
                proofElement.ValueKind != JsonValueKind.Object)
            {
                return new ConsensusProducerInfo(null);
            }

            var proof = new ContributionProofModel
            {
                ProjectId = ReadString(proofElement, "projectId", "ProjectId"),
                Epoch = ReadInt64(proofElement, "epoch", "Epoch"),
                ProducerPublicKey = ReadString(proofElement, "producerPublicKey", "ProducerPublicKey"),
                ProducerScore = ReadInt32(proofElement, "producerScore", "ProducerScore"),
                ScoreSnapshotHash = ReadString(proofElement, "scoreSnapshotHash", "ScoreSnapshotHash")
            };

            if (TryGetProperty(proofElement, "evidenceBlockHashes", "EvidenceBlockHashes", out var evidenceElement) &&
                evidenceElement.ValueKind == JsonValueKind.Array)
            {
                proof.EvidenceBlockHashes.AddRange(
                    evidenceElement.EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty)
                        .Where(item => !string.IsNullOrWhiteSpace(item)));
            }

            return string.IsNullOrWhiteSpace(proof.ProducerPublicKey)
                ? new ConsensusProducerInfo(null)
                : new ConsensusProducerInfo(proof);
        }
        catch
        {
            return new ConsensusProducerInfo(null);
        }
    }

    private static bool TryGetProperty(JsonElement element, string camelName, string pascalName, out JsonElement value)
    {
        return element.TryGetProperty(camelName, out value) ||
               element.TryGetProperty(pascalName, out value);
    }

    private static string ReadString(JsonElement element, string camelName, string pascalName) =>
        TryGetProperty(element, camelName, pascalName, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static int ReadInt32(JsonElement element, string camelName, string pascalName) =>
        TryGetProperty(element, camelName, pascalName, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static long ReadInt64(JsonElement element, string camelName, string pascalName) =>
        TryGetProperty(element, camelName, pascalName, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static bool ReadBool(JsonElement element, string camelName, string pascalName) =>
        TryGetProperty(element, camelName, pascalName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;
}
