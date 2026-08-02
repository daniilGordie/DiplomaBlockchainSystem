using System.Net.Http.Json;
using System.Text.Json;

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

    public async Task<IReadOnlyList<string>> FetchKnownChannelsOverHttpAsync(string peerUrl, CancellationToken cancellationToken = default)
    {
        string normalizedPeerUrl = P2POptions.NormalizeUrl(peerUrl);
        if (string.IsNullOrWhiteSpace(normalizedPeerUrl) || IsIrohPeerUrl(normalizedPeerUrl))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var response = await _httpClient.GetAsync($"{normalizedPeerUrl}/api/network/status", cancellationToken);
            await EnsureSuccessAsync(response, "network/status", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("channels", out var channelsElement) ||
                channelsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var channels = new List<string>();
            foreach (var channel in channelsElement.EnumerateArray())
            {
                if (channel.TryGetProperty("channelId", out var id) ||
                    channel.TryGetProperty("ChannelId", out id))
                {
                    string? value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        channels.Add(value);
                    }
                }
            }

            return channels;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[P2P] Fetch known channels over HTTP from {PeerUrl} failed: {Message}", normalizedPeerUrl, ex.Message);
            return Array.Empty<string>();
        }
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
public sealed record IrohCommittedSinceRequest(string Peer, string ChannelId, int AfterIndex, string AfterHash, IrohMessageAuth Auth);
public sealed record IrohCommittedSincePayload(string ChannelId, int AfterIndex, string AfterHash, IrohMessageAuth Auth);
public sealed record IrohSubmitBlockRequest(string Peer, BlockModel Block, IrohMessageAuth Auth);
public sealed record IrohSubmitBlockPayload(BlockModel Block, IrohMessageAuth Auth);
public sealed record IrohKnownChannelsResponse(string[] ChannelIds);
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
