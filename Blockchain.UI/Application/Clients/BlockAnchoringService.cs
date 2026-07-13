using System.Security.Cryptography;
using System.Net.Http.Json;
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
            "Success. Block anchored via PoC.",
            "Rejected");
    }

    public async Task<BlockAnchorResult> AnchorJsonStringWithKeystoreAsync(
        string json,
        string targetChannel,
        string keystore,
        string password)
    {
        return await AnchorSerializedAsync(
            json,
            targetChannel,
            signableData => SignWithKeystoreAsync(keystore, password, signableData),
            "Work item successfully written to the blockchain.",
            "Node error");
    }

    private async Task<BlockAnchorResult> AnchorSerializedAsync(
        string json,
        string targetChannel,
        Func<string, Task<string>> signAsync,
        string successMessage,
        string failurePrefix)
    {
        var latestBlock = await LoadLatestAnchorBlockAsync(targetChannel);
        string prevHash = latestBlock != null ? latestBlock.Hash : "0";
        int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
        long timestampUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds).UtcDateTime.ToString("O");
        string publicKey = _keyService.PublicKey ?? "";
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
            ChannelId = targetChannel
        };

        var consensus = await _consensusClient.GetProducerInfoAsync(_nodeUrl, targetChannel);
        await FinalizeBlockHashLocal(block, consensus.RequireProofOfWork);
        if (consensus.ContributionProof != null)
        {
            block.ContributionProof = consensus.ContributionProof;
        }

        var intentResponse = await SubmitSignedIntentAsync(json, targetChannel, publicKey, timestampUnixSeconds, signAsync, block);
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
        string projectId = string.Equals(targetChannel, "System", StringComparison.OrdinalIgnoreCase)
            ? ExtractProjectId(payloadJson)
            : targetChannel;
        string intentId = ComputeIntentId(
            networkId,
            projectId,
            targetChannel,
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
            targetChannel,
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
                targetChannel,
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
            var response = await http.PostAsJsonAsync("/api/network/intents/submit", request);
            var body = await response.Content.ReadFromJsonAsync<SignedIntentSubmitResponse>();
            return body ?? new SignedIntentSubmitResponse(false, $"Intent submit failed with HTTP {(int)response.StatusCode}.", intentId, targetChannel, "FailedRetryable", "");
        }
        catch (Exception ex)
        {
            return new SignedIntentSubmitResponse(false, $"Intent submit failed: {ex.Message}", intentId, targetChannel, "FailedRetryable", "");
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
