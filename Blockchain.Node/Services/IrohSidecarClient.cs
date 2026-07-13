using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public sealed class IrohSidecarClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly P2POptions _options;
    private readonly IrohMessageSecurity _messageSecurity;
    private readonly ILogger<IrohSidecarClient> _logger;

    public IrohSidecarClient(
        HttpClient httpClient,
        Microsoft.Extensions.Options.IOptions<P2POptions> options,
        IrohMessageSecurity messageSecurity,
        ILogger<IrohSidecarClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _messageSecurity = messageSecurity;
        _logger = logger;
    }

    public bool Enabled => _options.Iroh.Enabled && !string.IsNullOrWhiteSpace(_options.Iroh.NormalizedSidecarUrl);

    public async Task<IrohStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled) return null;

        try
        {
            return await _httpClient.GetFromJsonAsync<IrohStatus>(
                $"{_options.Iroh.NormalizedSidecarUrl}/status",
                JsonOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Iroh] Sidecar status request failed: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<string> GetPublicUrlAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(status?.PublicUrl))
        {
            return status.PublicUrl;
        }

        return string.IsNullOrWhiteSpace(status?.NodeId) ? string.Empty : $"iroh://{status.NodeId}";
    }

    public async Task BroadcastBlockAsync(IEnumerable<string> peerUrls, Block block, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;

        var peers = peerUrls
            .Select(ParseIrohPeerAddress)
            .Where(peer => !string.IsNullOrWhiteSpace(peer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (peers.Length == 0) return;

        var request = new IrohBroadcastBlockRequest(peers, GrpcProjectMapper.ToBlockModel(block));
        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Iroh.NormalizedSidecarUrl}/broadcast-block",
            request,
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("[Iroh] Broadcast failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
    }

    public async Task<IReadOnlyList<string>> FetchKnownChannelsAsync(string peerUrl, CancellationToken cancellationToken = default)
    {
        string peer = ParseIrohPeerAddress(peerUrl);
        if (!Enabled || string.IsNullOrWhiteSpace(peer)) return Array.Empty<string>();

        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Iroh.NormalizedSidecarUrl}/known-channels",
            new IrohPeerRequest(peer),
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, "known-channels", cancellationToken);

        var channels = await response.Content.ReadFromJsonAsync<IrohKnownChannelsResponse>(JsonOptions, cancellationToken);
        return channels?.ChannelIds ?? Array.Empty<string>();
    }

    public async Task<IReadOnlyList<BlockModel>> FetchChainAsync(string peerUrl, string channelId, CancellationToken cancellationToken = default)
    {
        string peer = ParseIrohPeerAddress(peerUrl);
        if (!Enabled || string.IsNullOrWhiteSpace(peer)) return Array.Empty<BlockModel>();

        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Iroh.NormalizedSidecarUrl}/chain",
            new IrohChainRequest(peer, channelId),
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, $"chain/{channelId}", cancellationToken);

        var chain = await response.Content.ReadFromJsonAsync<IrohChainResponse>(JsonOptions, cancellationToken);
        return chain?.Blocks ?? Array.Empty<BlockModel>();
    }

    public async Task<IReadOnlyList<IrohCommittedBlockEnvelope>> FetchCommittedSinceAsync(
        string peerUrl,
        string channelId,
        int afterIndex,
        string afterHash,
        CancellationToken cancellationToken = default)
    {
        string peer = ParseIrohPeerAddress(peerUrl);
        if (!Enabled || string.IsNullOrWhiteSpace(peer)) return Array.Empty<IrohCommittedBlockEnvelope>();

        string normalizedChannelId = string.IsNullOrWhiteSpace(channelId) ? "System" : channelId;
        var auth = _messageSecurity.CreateAuth(
            IrohMessageOperations.CommittedSince,
            IrohMessageSecurity.HashPayload($"{normalizedChannelId}|{afterIndex}|{afterHash}"));

        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Iroh.NormalizedSidecarUrl}/committed-since",
            new IrohCommittedSinceRequest(peer, normalizedChannelId, afterIndex, afterHash, auth),
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, $"committed-since/{channelId}", cancellationToken);

        var blocks = await response.Content.ReadFromJsonAsync<IrohCommittedSinceResponse>(JsonOptions, cancellationToken);
        return blocks?.Blocks ?? Array.Empty<IrohCommittedBlockEnvelope>();
    }

    public async Task<IReadOnlyList<IrohCommittedBlockEnvelope>> FetchCommittedSinceOverHttpAsync(
        string peerUrl,
        string channelId,
        int afterIndex,
        string afterHash,
        CancellationToken cancellationToken = default)
    {
        string normalizedPeerUrl = P2POptions.NormalizeUrl(peerUrl);
        if (string.IsNullOrWhiteSpace(normalizedPeerUrl) || IsIrohPeerUrl(normalizedPeerUrl))
        {
            return Array.Empty<IrohCommittedBlockEnvelope>();
        }

        string normalizedChannelId = string.IsNullOrWhiteSpace(channelId) ? "System" : channelId;
        var auth = _messageSecurity.CreateAuth(
            IrohMessageOperations.CommittedSince,
            IrohMessageSecurity.HashPayload($"{normalizedChannelId}|{afterIndex}|{afterHash}"));

        var response = await _httpClient.PostAsJsonAsync(
            $"{normalizedPeerUrl}/api/network/committed-since",
            new IrohCommittedSincePayload(normalizedChannelId, afterIndex, afterHash, auth),
            JsonOptions,
            cancellationToken);
        await EnsureSuccessAsync(response, $"network/committed-since/{channelId}", cancellationToken);

        var blocks = await response.Content.ReadFromJsonAsync<IrohCommittedSinceResponse>(JsonOptions, cancellationToken);
        return blocks?.Blocks ?? Array.Empty<IrohCommittedBlockEnvelope>();
    }

    public async Task<IrohSubmitBlockResponse> SubmitBlockAsync(string peerUrl, BlockModel block, CancellationToken cancellationToken = default)
    {
        string peer = ParseIrohPeerAddress(peerUrl);
        if (!Enabled || string.IsNullOrWhiteSpace(peer))
        {
            return new IrohSubmitBlockResponse(false, "Iroh sidecar is disabled or peer URL is not an iroh:// URL.", block.ChannelId);
        }

        var auth = _messageSecurity.CreateAuth(
            IrohMessageOperations.SubmitBlock,
            IrohMessageSecurity.HashPayload(block.Hash));

        var response = await _httpClient.PostAsJsonAsync(
            $"{_options.Iroh.NormalizedSidecarUrl}/submit-block",
            new IrohSubmitBlockRequest(peer, block, auth),
            JsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("[Iroh] SubmitBlock failed with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
            return new IrohSubmitBlockResponse(false, $"Iroh SubmitBlock failed with HTTP {(int)response.StatusCode}: {body}", block.ChannelId);
        }

        return await response.Content.ReadFromJsonAsync<IrohSubmitBlockResponse>(JsonOptions, cancellationToken)
            ?? new IrohSubmitBlockResponse(false, "Iroh SubmitBlock returned an empty response.", block.ChannelId);
    }

    public async Task<IReadOnlyList<BlockModel>> DrainEventsAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled) return Array.Empty<BlockModel>();

        var events = await _httpClient.GetFromJsonAsync<IrohEventsResponse>(
            $"{_options.Iroh.NormalizedSidecarUrl}/events?limit=100",
            JsonOptions,
            cancellationToken);
        return events?.Blocks ?? Array.Empty<BlockModel>();
    }

    public static bool IsIrohPeerUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith("iroh://", StringComparison.OrdinalIgnoreCase);

    public static string ParseIrohPeerAddress(string url) =>
        IsIrohPeerUrl(url) ? url["iroh://".Length..].Trim().TrimEnd('/') : string.Empty;

    public static string ParseIrohPeerId(string url)
    {
        string peerAddress = ParseIrohPeerAddress(url);
        int queryIndex = peerAddress.IndexOf('?', StringComparison.Ordinal);
        return queryIndex >= 0 ? peerAddress[..queryIndex] : peerAddress;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("[Iroh] {Operation} failed with HTTP {StatusCode}: {Body}", operation, (int)response.StatusCode, body);
        throw new HttpRequestException($"Iroh sidecar {operation} failed with HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }
}

public sealed record IrohStatus(
    string NodeId,
    string PublicUrl,
    string[] DirectAddresses,
    string RelayUrl,
    string ConnectionPath = "",
    string TransportMode = "");
public sealed record IrohPeerRequest(string Peer);
public sealed record IrohChainRequest(string Peer, string ChannelId);
public sealed record IrohCommittedSinceRequest(string Peer, string ChannelId, int AfterIndex, string AfterHash, IrohMessageAuth Auth);
public sealed record IrohCommittedSincePayload(string ChannelId, int AfterIndex, string AfterHash, IrohMessageAuth Auth);
public sealed record IrohSubmitBlockRequest(string Peer, BlockModel Block, IrohMessageAuth Auth);
public sealed record IrohSubmitBlockPayload(BlockModel Block, IrohMessageAuth Auth);
public sealed record IrohBroadcastBlockRequest(string[] Peers, BlockModel Block);
public sealed record IrohKnownChannelsResponse(string[] ChannelIds);
public sealed record IrohChainResponse(BlockModel[] Blocks);
public sealed record IrohEventsResponse(BlockModel[] Blocks);
public sealed record IrohSubmitBlockResponse(bool Success, string Message, string ChannelId);
public sealed record IrohCommittedSinceResponse(IrohCommittedBlockEnvelope[] Blocks);
public sealed record IrohCommittedBlockEnvelope(BlockModel Block, IrohFinalityMetadataModel Finality);
public sealed record IrohFinalityMetadataModel(
    string BlockHash,
    string ChannelId,
    string FinalityMode,
    long? RaftLogIndex,
    long? RaftTerm,
    DateTime CommittedAtUtc);

public static class IrohMessageOperations
{
    public const string SubmitBlock = "SubmitBlock";
    public const string CommittedSince = "CommittedSince";
}
