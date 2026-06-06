using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public sealed class IrohSidecarClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly P2POptions _options;
    private readonly ILogger<IrohSidecarClient> _logger;

    public IrohSidecarClient(HttpClient httpClient, Microsoft.Extensions.Options.IOptions<P2POptions> options, ILogger<IrohSidecarClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
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

public sealed record IrohStatus(string NodeId, string PublicUrl, string[] DirectAddresses, string RelayUrl);
public sealed record IrohPeerRequest(string Peer);
public sealed record IrohChainRequest(string Peer, string ChannelId);
public sealed record IrohBroadcastBlockRequest(string[] Peers, BlockModel Block);
public sealed record IrohKnownChannelsResponse(string[] ChannelIds);
public sealed record IrohChainResponse(BlockModel[] Blocks);
public sealed record IrohEventsResponse(BlockModel[] Blocks);
