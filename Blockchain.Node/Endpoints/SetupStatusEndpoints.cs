using Blockchain.Node.Services;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Blockchain.Node.Endpoints;

public static class SetupStatusEndpoints
{
    public static IEndpointRouteBuilder MapSetupStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/status", async (
            IOptions<P2POptions> p2p,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar,
            IOptions<NodeVersionOptions> versionOptions) =>
        {
            var options = p2p.Value;
            var irohStatus = options.Iroh.Enabled
                ? await irohSidecar.GetStatusAsync()
                : null;

            return Results.Json(new SetupStatusResponse(
                options.EffectiveNodeId,
                options.Role.ToString(),
                options.NormalizedPublicUrl,
                options.NormalizedBootstrapPeers.ToArray(),
                !string.IsNullOrWhiteSpace(options.SyncToken),
                !string.IsNullOrWhiteSpace(options.EffectiveIdentityKeyPath),
                Fingerprint(nodeIdentity.PublicKey),
                options.AllowRegistrationTokenFallback,
                options.Iroh.Enabled,
                options.Iroh.NormalizedSidecarUrl,
                irohStatus != null,
                irohStatus?.NodeId ?? string.Empty,
                irohStatus?.RelayUrl ?? string.Empty,
                Math.Clamp(options.DiscoveryIntervalSeconds, 10, 3600),
                versionOptions.Value.NodeVersion,
                versionOptions.Value.ProtocolVersion));
        });

        endpoints.MapGet("/api/setup/version", (IOptions<NodeVersionOptions> versionOptions) =>
        {
            var version = versionOptions.Value;
            return Results.Json(version);
        });

        endpoints.MapGet("/api/setup/migrations", async (
            IConfiguration configuration,
            IOptions<P2POptions> p2p,
            IrohSidecarClient irohSidecar) =>
        {
            var options = p2p.Value;
            string dbPath = configuration.GetConnectionString("DefaultNodeDb") ?? "nexus_node_5041.db";
            var checks = new List<MigrationCheck>
            {
                new("database-path", !string.IsNullOrWhiteSpace(dbPath), dbPath),
                new("node-identity-path", !string.IsNullOrWhiteSpace(options.EffectiveIdentityKeyPath), options.EffectiveIdentityKeyPath),
                new("sync-token", !string.IsNullOrWhiteSpace(options.SyncToken), "configured=" + !string.IsNullOrWhiteSpace(options.SyncToken)),
                new("signed-registration", !options.AllowRegistrationTokenFallback, "fallback=" + options.AllowRegistrationTokenFallback),
                new("bootstrap-peers", options.Role == P2PNodeRole.Bootstrap || options.NormalizedBootstrapPeers.Count > 0, string.Join(",", options.NormalizedBootstrapPeers)),
            };

            if (options.Iroh.Enabled)
            {
                var status = await irohSidecar.GetStatusAsync();
                checks.Add(new("iroh-sidecar", status != null, status?.PublicUrl ?? "unavailable"));
                checks.Add(new("iroh-relay", !string.IsNullOrWhiteSpace(status?.RelayUrl), status?.RelayUrl ?? "none"));
            }

            return Results.Json(new MigrationChecklistResponse(checks));
        });

        endpoints.MapPost("/api/setup/plan", (SetupPlanRequest request) =>
        {
            var validation = ValidateSetupPlan(request);
            if (validation.Count > 0)
            {
                return Results.BadRequest(new SetupPlanResponse(false, NormalizeMode(request.Mode), "", "", validation));
            }

            string mode = NormalizeMode(request.Mode);
            string fileName = mode == "bootstrap" ? "bootstrap-node.env" : mode == "full-node" ? "full-node.env" : "local-node.env";
            string envContent = BuildSetupEnv(mode, request);
            return Results.Json(new SetupPlanResponse(true, mode, fileName, envContent, BuildSetupWarnings(mode, request)));
        });

        endpoints.MapGet("/api/setup/update-check", async (
            IOptions<NodeVersionOptions> versionOptions,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var current = versionOptions.Value;
            if (string.IsNullOrWhiteSpace(current.UpdateManifestUrl))
            {
                return Results.Json(UpdateCheckResponse.NotConfigured(current));
            }

            try
            {
                using var http = httpClientFactory.CreateClient();
                var manifest = await http.GetFromJsonAsync<UpdateManifest>(current.UpdateManifestUrl, cancellationToken);
                if (manifest == null)
                {
                    return Results.Json(UpdateCheckResponse.Failed(current, "Update manifest returned an empty response."));
                }

                bool updateAvailable = IsDifferent(current.NodeVersion, manifest.NodeVersion);
                bool protocolCompatible = string.Equals(current.ProtocolVersion, manifest.ProtocolVersion, StringComparison.OrdinalIgnoreCase);
                bool sidecarCompatible = string.IsNullOrWhiteSpace(manifest.IrohSidecarVersion)
                    || string.Equals(current.IrohSidecarVersion, manifest.IrohSidecarVersion, StringComparison.OrdinalIgnoreCase);
                string warning = protocolCompatible && sidecarCompatible
                    ? string.Empty
                    : "Update requires coordinated node, protocol, or Iroh sidecar migration.";

                return Results.Json(new UpdateCheckResponse(
                    true,
                    current.NodeVersion,
                    manifest.NodeVersion,
                    current.ProtocolVersion,
                    manifest.ProtocolVersion,
                    current.IrohSidecarVersion,
                    manifest.IrohSidecarVersion,
                    updateAvailable,
                    protocolCompatible,
                    sidecarCompatible,
                    current.UpdateManifestUrl,
                    warning));
            }
            catch (Exception ex)
            {
                return Results.Json(UpdateCheckResponse.Failed(current, ex.Message));
            }
        });

        return endpoints;
    }

    private static List<string> ValidateSetupPlan(SetupPlanRequest request)
    {
        var errors = new List<string>();
        string mode = NormalizeMode(request.Mode);
        if (mode is not ("local" or "full-node" or "bootstrap"))
        {
            errors.Add("Choose Local node, Join network, or Bootstrap node.");
        }

        if (mode == "bootstrap" && !IsHttpUrl(request.PublicUrl))
        {
            errors.Add("Bootstrap mode requires a public HTTP or HTTPS URL.");
        }

        if (mode == "full-node" && !IsHttpUrl(request.BootstrapGrpcUrl))
        {
            errors.Add("Join network mode requires a bootstrap gRPC HTTP or HTTPS URL.");
        }

        string relayMode = NormalizeRelayMode(request.RelayMode);
        if (relayMode is not ("default" or "staging" or "disabled"))
        {
            errors.Add("Relay mode must be default, staging, or disabled.");
        }

        return errors;
    }

    private static IReadOnlyList<string> BuildSetupWarnings(string mode, SetupPlanRequest request)
    {
        var warnings = new List<string>();
        string relayMode = NormalizeRelayMode(request.RelayMode);
        if (mode == "local")
        {
            warnings.Add("Local mode is isolated and will not join the shared blockchain network.");
        }

        if (mode == "full-node" && relayMode == "disabled")
        {
            warnings.Add("Disabled Iroh relay mode is not suitable for machines behind NAT.");
        }

        if (mode == "bootstrap" && request.PublicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Public bootstrap nodes should use HTTPS in production.");
        }

        return warnings;
    }

    private static string BuildSetupEnv(string mode, SetupPlanRequest request)
    {
        string nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? NewNodeId(mode) : request.NodeId.Trim();
        string relayMode = NormalizeRelayMode(request.RelayMode);
        var lines = new List<string>
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            "NODE_DB_PASSWORD=" + NewSecret(),
            "NODE_ADMIN_TOKEN=" + NewSecret(),
            "WEBHOOK_SECRET=" + NewSecret(),
            "ORACLE_PUBLIC_KEY=" + (string.IsNullOrWhiteSpace(request.OraclePublicKey) ? "auto" : request.OraclePublicKey.Trim()),
            "P2P_NODE_ID=" + nodeId,
            "P2P_SYNC_TOKEN=" + NewSecret(),
            "P2P_REGISTRATION_TOKEN=",
            "P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK=false",
            "P2P_IDENTITY_KEY_PATH=/data/node-identity.p256.key",
            "P2P_DISCOVERY_INTERVAL_SECONDS=60"
        };

        if (mode == "bootstrap")
        {
            lines.Add("NODE_HTTP_PORT=7041");
            lines.Add("NODE_GRPC_PORT=7141");
            lines.Add("P2P_PUBLIC_URL=" + request.PublicUrl.Trim());
            lines.Add("P2P_MAX_REGISTERED_PEERS=5000");
            lines.Add("P2P_MAX_REGISTRATIONS_PER_MINUTE_PER_ADDRESS=30");
        }
        else
        {
            lines.Add("NODE_HTTP_PORT=7042");
            lines.Add("NODE_GRPC_PORT=7142");
            lines.Add("P2P_BOOTSTRAP_GRPC_URL=" + (mode == "local" ? "" : request.BootstrapGrpcUrl.Trim()));
            lines.Add("IROH_LOCAL_API_TOKEN=" + NewSecret());
            lines.Add("IROH_SECRET_KEY_PATH=/data/iroh-secret.key");
            lines.Add("IROH_RELAY_MODE=" + relayMode);
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string NormalizeMode(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "join" or "full" or "fullnode" or "full-node" => "full-node",
            "boot" or "bootstrap" or "bootstrap-node" => "bootstrap",
            "local" or "local-node" => "local",
            var value => value
        };

    private static string NormalizeRelayMode(string? relayMode) =>
        string.IsNullOrWhiteSpace(relayMode) ? "default" : relayMode.Trim().ToLowerInvariant();

    private static string NewNodeId(string mode)
    {
        string value = $"{mode}-{Guid.NewGuid():N}";
        return value[..Math.Min(value.Length, mode.Length + 9)];
    }

    private static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static bool IsDifferent(string current, string latest) =>
        !string.IsNullOrWhiteSpace(latest)
        && !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string publicKey)
    {
        byte[] raw = Convert.FromBase64String(publicKey);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}

public sealed record SetupStatusResponse(
    string NodeId,
    string Role,
    string PublicUrl,
    string[] BootstrapPeers,
    bool SyncTokenConfigured,
    bool NodeIdentityConfigured,
    string NodeIdentityFingerprint,
    bool RegistrationTokenFallbackEnabled,
    bool IrohEnabled,
    string IrohSidecarUrl,
    bool IrohSidecarHealthy,
    string IrohNodeId,
    string IrohRelayUrl,
    int DiscoveryIntervalSeconds,
    string NodeVersion,
    string ProtocolVersion);

public sealed record MigrationChecklistResponse(IReadOnlyList<MigrationCheck> Checks);

public sealed record MigrationCheck(string Name, bool Passed, string Detail);

public sealed record SetupPlanRequest(
    string Mode,
    string PublicUrl,
    string BootstrapGrpcUrl,
    string NodeId,
    string RelayMode,
    string OraclePublicKey);

public sealed record SetupPlanResponse(
    bool Valid,
    string Mode,
    string FileName,
    string EnvContent,
    IReadOnlyList<string> Messages);

public sealed record UpdateManifest(
    string NodeVersion,
    string ProtocolVersion,
    string IrohSidecarVersion);

public sealed record UpdateCheckResponse(
    bool Configured,
    string CurrentNodeVersion,
    string LatestNodeVersion,
    string CurrentProtocolVersion,
    string LatestProtocolVersion,
    string CurrentIrohSidecarVersion,
    string LatestIrohSidecarVersion,
    bool UpdateAvailable,
    bool ProtocolCompatible,
    bool IrohSidecarCompatible,
    string ManifestUrl,
    string Warning)
{
    public static UpdateCheckResponse NotConfigured(NodeVersionOptions current) => new(
        false,
        current.NodeVersion,
        "",
        current.ProtocolVersion,
        "",
        current.IrohSidecarVersion,
        "",
        false,
        true,
        true,
        "",
        "Update manifest URL is not configured.");

    public static UpdateCheckResponse Failed(NodeVersionOptions current, string warning) => new(
        true,
        current.NodeVersion,
        "",
        current.ProtocolVersion,
        "",
        current.IrohSidecarVersion,
        "",
        false,
        false,
        false,
        current.UpdateManifestUrl,
        warning);
}
