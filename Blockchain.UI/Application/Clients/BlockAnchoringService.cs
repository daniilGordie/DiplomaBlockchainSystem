using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Blockchain.Node;
using Blockchain.UI.Application.Security;
using Blockchain.UI.Infrastructure.Grpc;
using Blockchain.UI.Services;
using Microsoft.AspNetCore.Components;

namespace Blockchain.UI.Application.Clients;

public sealed class BlockAnchoringService : IBlockAnchoringService
{
    private readonly BlockchainService.BlockchainServiceClient _blockchainClient;
    private readonly IReadRequestAuthorizer _readAuthorizer;
    private readonly KeyService _keyService;
    private readonly IConsensusClient _consensusClient;
    private readonly string _nodeUrl;

    public BlockAnchoringService(
        BlockchainService.BlockchainServiceClient blockchainClient,
        IReadRequestAuthorizer readAuthorizer,
        KeyService keyService,
        IConsensusClient consensusClient,
        IConfiguration configuration,
        NavigationManager navigation)
    {
        _blockchainClient = blockchainClient;
        _readAuthorizer = readAuthorizer;
        _keyService = keyService;
        _consensusClient = consensusClient;
        _nodeUrl = NodeUrlResolver.Resolve(configuration, navigation);
    }

    public async Task<UserNameAvailabilityResult> CheckUserNameAvailabilityAsync(string userName)
    {
        var response = await _blockchainClient.CheckUserNameAsync(new UserNameRequest
        {
            UserName = userName?.Trim() ?? string.Empty
        });

        return new UserNameAvailabilityResult(
            response.Success,
            response.Exists,
            response.Message);
    }

    public async Task<UserIdentityCheckResult> CheckCurrentUserIdentityAsync()
    {
        var request = new UserRequest
        {
            UserName = _keyService.UserName,
            UserPublicKey = _keyService.PublicKey ?? ""
        };

        _readAuthorizer.Apply(request, $"USER:{_keyService.UserName}:IDENTITY");
        var response = await _blockchainClient.GetUserIdentityAsync(request);
        return new UserIdentityCheckResult(
            response.Success,
            response.Exists,
            response.PublicKeyMatches,
            response.Message);
    }

    public async Task<BlockAnchorResult> AnchorJsonStringAsync(string json, string targetChannel)
    {
        return await AnchorSerializedAsync(
            json,
            targetChannel,
            signableData => Task.FromResult(_keyService.SignData(signableData)),
            _keyService.PublicKey,
            "Success. Block anchored via PoC.",
            "Rejected");
    }

    public async Task<BlockAnchorResult> AnchorJsonStringWithKeystoreAsync(
        string json,
        string targetChannel,
        string keystore,
        string password,
        string? signerPublicKey = null)
    {
        return await AnchorSerializedAsync(
            json,
            targetChannel,
            signableData => SignWithKeystoreAsync(keystore, password, signableData),
            signerPublicKey,
            "Work item successfully written to the blockchain.",
            "Node error");
    }

    private async Task<BlockAnchorResult> AnchorSerializedAsync(
        string json,
        string targetChannel,
        Func<string, Task<string>> signAsync,
        string? signerPublicKey,
        string successMessage,
        string failurePrefix)
    {
        string safeTargetChannel = NormalizeChannelId(targetChannel);
        var latestBlock = await LoadLatestAnchorBlockAsync(safeTargetChannel);
        string prevHash = latestBlock != null ? latestBlock.Hash : "0";
        int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
        long timestampUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds).UtcDateTime.ToString("O");
        string publicKey = string.IsNullOrWhiteSpace(signerPublicKey)
            ? _keyService.PublicKey ?? ""
            : signerPublicKey.Trim();
        string signableData = $"{expectedIndex}{timestampUnixSeconds}{json}{prevHash}";
        string signature = await signAsync(signableData);

        var block = new BlockModel
        {
            Index = expectedIndex,
            Data = json,
            Timestamp = timestamp,
            TimestampUnixSeconds = timestampUnixSeconds,
            PreviousHash = prevHash,
            Hash = ComputeSimpleHash(signableData),
            ValidatorPublicKey = publicKey,
            Signature = signature,
            ChannelId = safeTargetChannel
        };

        var consensus = await _consensusClient.GetProducerInfoAsync(_nodeUrl, safeTargetChannel);
        await FinalizeBlockHashLocal(block, consensus.RequireProofOfWork);
        if (consensus.ContributionProof != null)
        {
            block.ContributionProof = consensus.ContributionProof;
        }

        var intentResponse = await SubmitSignedIntentAsync(json, safeTargetChannel, publicKey, timestampUnixSeconds, signAsync, block);
        if (intentResponse != null)
        {
            return intentResponse.Success
                ? new BlockAnchorResult(true, successMessage)
                : new BlockAnchorResult(false, $"{failurePrefix}: {intentResponse.Message}");
        }

        return new BlockAnchorResult(
            false,
            $"{failurePrefix}: signed intent endpoint is unavailable. The action was not submitted through the legacy block path.");
    }

    private async Task<SignedIntentSubmitResponse?> SubmitSignedIntentAsync(
        string payloadJson,
        string targetChannel,
        string actorPublicKey,
        long timestampUnixSeconds,
        Func<string, Task<string>> signAsync,
        BlockModel block)
    {
        string networkId = await LoadNetworkIdAsync();
        string operationType = ExtractOperationType(payloadJson);
        string correlationId = Guid.NewGuid().ToString("N");
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        int schemaVersion = 1;
        string safeTargetChannel = NormalizeChannelId(targetChannel);
        string projectId = string.Equals(safeTargetChannel, "System", StringComparison.OrdinalIgnoreCase)
            ? ExtractProjectId(payloadJson)
            : safeTargetChannel;
        string intentId = ComputeIntentId(
            networkId,
            projectId,
            safeTargetChannel,
            operationType,
            payloadJson,
            actorPublicKey,
            timestampUnixSeconds,
            nonce,
            correlationId,
            schemaVersion);
        string canonical = BuildIntentSignableData(
            networkId,
            projectId,
            safeTargetChannel,
            operationType,
            payloadJson,
            actorPublicKey,
            timestampUnixSeconds,
            nonce,
            correlationId,
            schemaVersion);
        string intentSignature = await signAsync(canonical);

        var request = new SignedIntentSubmitRequest(
            new SignedIntentDto(
                intentId,
                networkId,
                projectId,
                safeTargetChannel,
                operationType,
                payloadJson,
                actorPublicKey,
                timestampUnixSeconds,
                nonce,
                intentSignature,
                correlationId,
                schemaVersion),
            block);

        using var http = new HttpClient { BaseAddress = new Uri(_nodeUrl.TrimEnd('/') + "/") };
        try
        {
            using var content = new StringContent(BuildSubmitIntentJson(request), Encoding.UTF8, "application/json");
            var response = await http.PostAsync("/api/network/intents/submit", content);
            string bodyJson = await response.Content.ReadAsStringAsync();
            var body = ParseSubmitResponse(bodyJson);
            if (body != null)
            {
                return body.Success || response.IsSuccessStatusCode
                    ? body
                    : body with { Message = $"HTTP {(int)response.StatusCode}: {body.Message}" };
            }

            return new SignedIntentSubmitResponse(false, $"Intent submit failed with HTTP {(int)response.StatusCode}: {bodyJson}", intentId, safeTargetChannel, "FailedRetryable", "");
        }
        catch (Exception ex)
        {
            return new SignedIntentSubmitResponse(false, $"Intent submit failed: {ex.Message}", intentId, safeTargetChannel, "FailedRetryable", "");
        }
    }

    private async Task<string> LoadNetworkIdAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_nodeUrl.TrimEnd('/') + "/") };
        try
        {
            var status = await http.GetFromJsonAsync<NetworkStatusProbe>("/api/network/status");
            return string.IsNullOrWhiteSpace(status?.NetworkId) ? "nexus-main" : status.NetworkId;
        }
        catch
        {
            return "nexus-main";
        }
    }

    private async Task<BlockModel?> LoadLatestAnchorBlockAsync(string targetChannel)
    {
        if (string.Equals(targetChannel, "System", StringComparison.OrdinalIgnoreCase))
        {
            var systemLatest = await _blockchainClient.GetLastBlockAsync(new EmptyRequest());
            return string.IsNullOrWhiteSpace(systemLatest.Hash) || systemLatest.Hash == "0"
                ? null
                : systemLatest;
        }

        var chainRequest = new ChainRequest
        {
            Count = 1,
            ChannelId = targetChannel,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? "",
            AfterIndex = -1
        };
        _readAuthorizer.Apply(chainRequest, $"CHAIN:{targetChannel}");
        var chainResponse = await _blockchainClient.GetChainAsync(chainRequest);

        return chainResponse.Blocks.Count > 0 ? chainResponse.Blocks.Last() : null;
    }

    private Task<string> SignWithKeystoreAsync(string keystore, string password, string signableData)
    {
        if (keystore.Contains("\"ProtectionMode\":\"passkey\"", StringComparison.OrdinalIgnoreCase))
        {
            return _keyService.SignDataWithPasskeyKeystoreAsync(keystore, signableData);
        }

        return Task.FromResult(_keyService.SignDataWithKeystore(keystore, password, signableData));
    }

    private static string ComputeSimpleHash(string data)
    {
        using var sha256 = SHA256.Create();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static string ComputeIntentId(
        string networkId,
        string projectId,
        string channelId,
        string operationType,
        string payloadJson,
        string actorPublicKey,
        long timestampUnixSeconds,
        string nonce,
        string correlationId,
        int schemaVersion)
    {
        string canonical = string.Join(
            ":",
            "NEXUS_INTENT_ID_V1",
            networkId,
            projectId,
            channelId,
            operationType,
            ComputeSimpleHash(payloadJson),
            actorPublicKey,
            timestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce,
            correlationId,
            schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ComputeSimpleHash(canonical);
    }

    private static string BuildIntentSignableData(
        string networkId,
        string projectId,
        string channelId,
        string operationType,
        string payloadJson,
        string actorPublicKey,
        long timestampUnixSeconds,
        string nonce,
        string correlationId,
        int schemaVersion)
    {
        return string.Join(
            ":",
            "NEXUS_INTENT_V1",
            networkId,
            projectId,
            channelId,
            operationType,
            ComputeSimpleHash(payloadJson),
            actorPublicKey,
            timestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce,
            correlationId,
            schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string ExtractOperationType(string payloadJson) =>
        ExtractPayloadString(payloadJson, "Type", "Action");

    private static string ExtractProjectId(string payloadJson) =>
        ExtractPayloadString(payloadJson, "ProjectId", "System");

    private static string NormalizeChannelId(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return "System";
        }

        var safeName = new string(channelId.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "System" : safeName;
    }

    private static string ExtractPayloadString(string payloadJson, string propertyName, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty(propertyName, out var value) ||
                doc.RootElement.TryGetProperty(char.ToLowerInvariant(propertyName[0]) + propertyName[1..], out value))
            {
                return value.GetString() ?? fallback;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static async Task FinalizeBlockHashLocal(BlockModel block, bool requireProofOfWork)
    {
        await Task.Delay(10);

        string timestampComponent = block.TimestampUnixSeconds > 0
            ? block.TimestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : DateTime.Parse(block.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind).ToString("s");

        block.Nonce = 0;
        using var sha256 = SHA256.Create();
        string baseData = $"{block.Index}{timestampComponent}{block.Data}{block.PreviousHash}{block.ValidatorPublicKey}{block.Signature}";

        if (!requireProofOfWork)
        {
            byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(baseData + block.Nonce));
            block.Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return;
        }

        while (true)
        {
            block.Nonce++;
            string rawData = baseData + block.Nonce;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawData);
            byte[] hashBytes = sha256.ComputeHash(bytes);
            block.Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (block.Hash.StartsWith("000", StringComparison.Ordinal))
            {
                break;
            }

            if (block.Nonce % 500 == 0)
            {
                await Task.Yield();
            }
        }
    }

    private static string BuildSubmitIntentJson(SignedIntentSubmitRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("intent");
            WriteIntent(writer, request.Intent);
            writer.WritePropertyName("block");
            WriteBlock(writer, request.Block);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteIntent(Utf8JsonWriter writer, SignedIntentDto intent)
    {
        writer.WriteStartObject();
        writer.WriteString("intentId", intent.IntentId);
        writer.WriteString("networkId", intent.NetworkId);
        writer.WriteString("projectId", intent.ProjectId);
        writer.WriteString("channelId", intent.ChannelId);
        writer.WriteString("operationType", intent.OperationType);
        writer.WriteString("payloadJson", intent.PayloadJson);
        writer.WriteString("actorPublicKey", intent.ActorPublicKey);
        writer.WriteNumber("timestampUnixSeconds", intent.TimestampUnixSeconds);
        writer.WriteString("nonce", intent.Nonce);
        writer.WriteString("signature", intent.Signature);
        writer.WriteString("correlationId", intent.CorrelationId);
        writer.WriteNumber("schemaVersion", intent.SchemaVersion);
        writer.WriteEndObject();
    }

    private static void WriteBlock(Utf8JsonWriter writer, BlockModel block)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", block.Index);
        writer.WriteString("timestamp", block.Timestamp);
        writer.WriteString("data", block.Data);
        writer.WriteString("previousHash", block.PreviousHash);
        writer.WriteString("hash", block.Hash);
        writer.WriteString("validatorPublicKey", block.ValidatorPublicKey);
        writer.WriteString("signature", block.Signature);
        writer.WriteNumber("nonce", block.Nonce);
        writer.WriteString("channelId", block.ChannelId);
        writer.WriteNumber("timestampUnixSeconds", block.TimestampUnixSeconds);
        if (block.ContributionProof != null)
        {
            writer.WritePropertyName("contributionProof");
            WriteContributionProof(writer, block.ContributionProof);
        }
        writer.WriteEndObject();
    }

    private static void WriteContributionProof(Utf8JsonWriter writer, ContributionProofModel proof)
    {
        writer.WriteStartObject();
        writer.WriteString("projectId", proof.ProjectId);
        writer.WriteNumber("epoch", proof.Epoch);
        writer.WriteString("producerPublicKey", proof.ProducerPublicKey);
        writer.WriteNumber("producerScore", proof.ProducerScore);
        writer.WriteString("scoreSnapshotHash", proof.ScoreSnapshotHash);
        writer.WritePropertyName("evidenceBlockHashes");
        writer.WriteStartArray();
        foreach (string hash in proof.EvidenceBlockHashes)
        {
            writer.WriteStringValue(hash);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static SignedIntentSubmitResponse? ParseSubmitResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new SignedIntentSubmitResponse(
            ReadBool(root, "success", "Success"),
            ReadString(root, "message", "Message"),
            ReadString(root, "intentId", "IntentId"),
            ReadString(root, "channelId", "ChannelId"),
            ReadString(root, "status", "Status"),
            ReadString(root, "committedBlockHash", "CommittedBlockHash"),
            ReadNullableInt64(root, "committedBlockIndex", "CommittedBlockIndex"),
            ReadString(root, "proposalId", "ProposalId"));
    }

    private static string ReadString(JsonElement element, string camelName, string pascalName) =>
        (element.TryGetProperty(camelName, out var value) || element.TryGetProperty(pascalName, out value)) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement element, string camelName, string pascalName) =>
        (element.TryGetProperty(camelName, out var value) || element.TryGetProperty(pascalName, out value)) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static long? ReadNullableInt64(JsonElement element, string camelName, string pascalName) =>
        (element.TryGetProperty(camelName, out var value) || element.TryGetProperty(pascalName, out value)) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long result)
            ? result
            : null;

    private sealed record NetworkStatusProbe(string NetworkId);

    private sealed record SignedIntentDto(
        string IntentId,
        string NetworkId,
        string ProjectId,
        string ChannelId,
        string OperationType,
        string PayloadJson,
        string ActorPublicKey,
        long TimestampUnixSeconds,
        string Nonce,
        string Signature,
        string CorrelationId,
        int SchemaVersion);

    private sealed record SignedIntentSubmitRequest(
        SignedIntentDto Intent,
        BlockModel Block);

    private sealed record SignedIntentSubmitResponse(
        bool Success,
        string Message,
        string IntentId,
        string ChannelId,
        string Status,
        string CommittedBlockHash,
        long? CommittedBlockIndex = null,
        string ProposalId = "");
}
