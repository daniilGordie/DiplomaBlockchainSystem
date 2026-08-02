using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blockchain.Application.Setup;

public sealed class NexusSetupUseCases
{
    public SetupStateResponse GetState(IReadOnlyDictionary<string, string?> configuration, bool fullNodeConfigured)
    {
        string networkId = Get(configuration, "Network:Id", Get(configuration, "NetworkId", string.Empty));
        string nodeId = Get(configuration, "P2P:NodeId", Get(configuration, "P2P_NODE_ID", string.Empty));
        string role = Get(configuration, "Node:Role", Get(configuration, "NODE_ROLE", string.Empty));
        string state = fullNodeConfigured
            ? "Ready"
            : string.IsNullOrWhiteSpace(networkId) && string.IsNullOrWhiteSpace(nodeId)
                ? "NotConfigured"
                : "AwaitingRestart";

        return new SetupStateResponse(state, networkId, nodeId, role, fullNodeConfigured);
    }

    public SetupApplyResponse CreateNetwork(CreateNetworkSetupRequest request)
    {
        string mode = NormalizeMode(request.Mode);
        if (mode is not ("bootstrap" or "all-in-one" or "local"))
        {
            return SetupApplyResponse.Failed("Create network mode must be Bootstrap, All-in-one, or Local private.");
        }

        string networkId = string.IsNullOrWhiteSpace(request.NetworkId) ? $"nexus-{Guid.NewGuid():N}" : request.NetworkId.Trim();
        string nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? NewNodeId(mode) : request.NodeId.Trim();
        bool local = mode == "local";
        bool raft = !local;
        string transport = NormalizeTransport(request.RaftTransport);
        if (transport == "Iroh" && string.IsNullOrWhiteSpace(request.IrohNodeId))
        {
            return SetupApplyResponse.Failed("Raft-over-Iroh requires local Iroh node id. Start the sidecar or choose TCP.");
        }

        if (transport == "Tcp" && raft && string.IsNullOrWhiteSpace(request.RaftPublicEndPoint))
        {
            return SetupApplyResponse.Failed("TCP Raft requires a public Raft endpoint.");
        }

        var config = BaseConfig(request.NetworkName, networkId, nodeId, local ? "Local" : "Bootstrap");
        config["Consensus:FinalityMode"] = local ? "Immediate" : "Raft";
        config["Consensus:EnableProofOfContributionValidation"] = (!local).ToString().ToLowerInvariant();
        config["P2P:Iroh:Enabled"] = (!local).ToString().ToLowerInvariant();
        config["P2P:Iroh:LocalApiToken"] = local ? string.Empty : NewSecret();
        config["P2P:PublicUrl"] = request.PublicHttpUrl ?? string.Empty;
        config["Network:BootstrapHttpUrl"] = request.PublicHttpUrl ?? string.Empty;
        config["Network:BootstrapGrpcUrl"] = request.PublicGrpcUrl ?? request.PublicHttpUrl ?? string.Empty;
        config["Network:TrustedBootstrapNodeId"] = nodeId;
        if (raft)
        {
            config["Raft:Transport"] = transport;
            config["Raft:NodeId"] = nodeId;
            config["Raft:PublicEndPoint"] = transport == "Tcp" ? request.RaftPublicEndPoint ?? string.Empty : string.Empty;
            config["Raft:IrohNodeId"] = transport == "Iroh" ? request.IrohNodeId ?? string.Empty : string.Empty;
            config["Raft:LogPath"] = "raft-log";
            config["Raft:UsePersistentMembership"] = "true";
            config["Raft:MembershipPath"] = "raft-membership";
            config["Raft:SnapshotPath"] = "raft-snapshots";
        }

        var invite = BuildInvite(networkId, request.NetworkName, nodeId, request.PublicHttpUrl, request.PublicGrpcUrl, request.BootstrapIrohUrl, transport);
        return SetupApplyResponse.Ok(config, invite, "AwaitingRestart");
    }

    public SetupApplyResponse JoinNetwork(JoinNetworkSetupRequest request)
    {
        var invite = DecodeInvite(request.Invite);
        if (invite == null)
        {
            return SetupApplyResponse.Failed("Connection invite is invalid.");
        }

        string role = NormalizeMode(request.Role) == "consensus" ? "ConsensusCandidate" : "Edge";
        string nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? NewNodeId("edge") : request.NodeId.Trim();
        var config = BaseConfig(invite.NetworkName, invite.NetworkId, nodeId, role == "Edge" ? "Edge" : "Edge");
        config["Consensus:FinalityMode"] = "Raft";
        config["Consensus:EnableProofOfContributionValidation"] = "true";
        config["P2P:Iroh:Enabled"] = "true";
        config["P2P:Iroh:LocalApiToken"] = NewSecret();
        config["P2P:BootstrapPeers:0"] = invite.BootstrapIrohUrl;
        config["Network:BootstrapHttpUrl"] = invite.BootstrapHttpUrl;
        config["Network:BootstrapGrpcUrl"] = invite.BootstrapGrpcUrl;
        config["Network:BootstrapIrohUrl"] = invite.BootstrapIrohUrl;
        config["Network:TrustedBootstrapNodeId"] = invite.BootstrapNodeId;
        config["Network:TrustedBootstrapFingerprint"] = invite.BootstrapFingerprint;
        return SetupApplyResponse.Ok(config, invite, "AwaitingApproval");
    }

    public InviteValidationResponse ValidateInvite(string inviteText)
    {
        var invite = DecodeInvite(inviteText);
        if (invite == null)
        {
            return new InviteValidationResponse(false, "Invalid invite.", null);
        }

        if (invite.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            return new InviteValidationResponse(false, "Invite expired.", invite);
        }

        if (!string.IsNullOrWhiteSpace(invite.BootstrapPublicKey))
        {
            return VerifyInviteSignature(invite)
                ? new InviteValidationResponse(true, "Invite is valid.", invite)
                : new InviteValidationResponse(false, "Invite signature is invalid.", invite);
        }

        if (!string.Equals(invite.Signature, SignInvitePlaceholder(invite), StringComparison.OrdinalIgnoreCase))
        {
            return new InviteValidationResponse(false, "Invite signature is invalid.", invite);
        }

        return new InviteValidationResponse(true, "Invite is valid.", invite);
    }

    public SetupApplyResponse CreateLocalNode(LocalNodeSetupRequest request)
    {
        return CreateNetwork(new CreateNetworkSetupRequest(
            string.IsNullOrWhiteSpace(request.NetworkName) ? "Local private node" : request.NetworkName,
            "local",
            request.NetworkId,
            request.NodeId,
            null,
            null,
            "Tcp",
            null,
            null,
            null));
    }

    public string SerializeConfig(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });

    private static Dictionary<string, string> BaseConfig(string? networkName, string networkId, string nodeId, string role) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Network:Name"] = string.IsNullOrWhiteSpace(networkName) ? "Nexus Network" : networkName.Trim(),
        ["Network:Id"] = networkId,
        ["Node:Role"] = role,
        ["NodeDbPassword"] = NewSecret(),
        ["NodeAdminToken"] = NewSecret(),
        ["WebhookSecret"] = NewSecret(),
        ["OraclePublicKey"] = "auto",
        ["OraclePrivateKeyPassword"] = NewSecret(),
        ["OracleKeyPath"] = "oracle_key.dat",
        ["Consensus:RequireProofOfWork"] = "false",
        ["Consensus:AcceptP2PBlocksAsFinal"] = "false",
        ["Consensus:ProducerKeyPath"] = "producer-key.dat",
        ["Consensus:ProducerPrivateKeyPassword"] = NewSecret(),
        ["P2P:NodeId"] = nodeId,
        ["P2P:IdentityKeyPath"] = "node-identity.p256.key",
        ["P2P:SyncToken"] = NewSecret(),
        ["P2P:DiscoveryIntervalSeconds"] = "60"
    };

    private static NetworkInviteDocument BuildInvite(
        string networkId,
        string? networkName,
        string bootstrapNodeId,
        string? bootstrapHttp,
        string? bootstrapGrpc,
        string? bootstrapIroh,
        string raftTransport)
    {
        var invite = new NetworkInviteDocument(
            1,
            "nexus-network/1",
            networkId,
            string.IsNullOrWhiteSpace(networkName) ? "Nexus Network" : networkName.Trim(),
            bootstrapNodeId,
            string.Empty,
            string.Empty,
            bootstrapIroh ?? string.Empty,
            bootstrapHttp ?? string.Empty,
            bootstrapGrpc ?? bootstrapHttp ?? string.Empty,
            new[] { raftTransport },
            "Edge",
            DateTimeOffset.UtcNow.AddDays(7),
            Guid.NewGuid().ToString("N"),
            string.Empty);
        return invite with { Signature = SignInvitePlaceholder(invite) };
    }

    private static NetworkInviteDocument? DecodeInvite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string text = value.Trim();
            string json = text.StartsWith('{')
                ? text
                : Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64Url(text)));
            return JsonSerializer.Deserialize<NetworkInviteDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static string SignInvitePlaceholder(NetworkInviteDocument invite)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BuildInviteCanonicalPayload(invite)))).ToLowerInvariant();
    }

    public static string BuildInviteCanonicalPayload(NetworkInviteDocument invite)
    {
        string transports = string.Join(',', invite.SupportedRaftTransports.Select(value => value.Trim()).Order(StringComparer.Ordinal));
        return string.Join('\n', new[]
        {
            "NEXUS_CONNECTION_INVITE_V1",
            invite.SchemaVersion.ToString(),
            invite.ProtocolVersion.Trim(),
            invite.NetworkId.Trim(),
            invite.NetworkName.Trim(),
            invite.BootstrapNodeId.Trim(),
            invite.BootstrapPublicKey.Trim(),
            invite.BootstrapIrohUrl.Trim(),
            invite.BootstrapHttpUrl.Trim(),
            invite.BootstrapGrpcUrl.Trim(),
            transports,
            invite.SuggestedRole.Trim(),
            invite.ExpiresAtUtc.ToUniversalTime().ToString("O"),
            invite.Nonce.Trim()
        });
    }

    private static bool VerifyInviteSignature(NetworkInviteDocument invite)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(invite.BootstrapPublicKey), out _);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(BuildInviteCanonicalPayload(invite)),
                Convert.FromBase64String(invite.Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string PadBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        int mod = padded.Length % 4;
        return mod == 0 ? padded : padded.PadRight(padded.Length + 4 - mod, '=');
    }

    private static string NormalizeMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "bootstrap+consensus" or "bootstrap-consensus" or "allinone" or "all-in-one" => "all-in-one",
        "boot" or "bootstrap" => "bootstrap",
        "join" or "edge" => "edge",
        "candidate" or "consensuscandidate" or "consensus-candidate" or "consensus" => "consensus",
        "local" or "local-private" => "local",
        var value => value
    };

    private static string NormalizeTransport(string? transport) =>
        string.Equals(transport, "Iroh", StringComparison.OrdinalIgnoreCase) ? "Iroh" : "Tcp";

    private static string NewNodeId(string mode)
    {
        string value = $"{NormalizeMode(mode).Replace("-", "")}-{Guid.NewGuid():N}";
        return value[..Math.Min(24, value.Length)];
    }
    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Get(IReadOnlyDictionary<string, string?> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}

public sealed record SetupStateResponse(string State, string NetworkId, string NodeId, string Role, bool FullNodeConfigured);

public sealed record CreateNetworkSetupRequest(
    string NetworkName,
    string Mode,
    string? NetworkId,
    string? NodeId,
    string? PublicHttpUrl,
    string? PublicGrpcUrl,
    string RaftTransport,
    string? RaftPublicEndPoint,
    string? IrohNodeId,
    string? BootstrapIrohUrl);

public sealed record JoinNetworkSetupRequest(string Invite, string Role, string? NodeId);
public sealed record LocalNodeSetupRequest(string? NetworkName, string? NetworkId, string? NodeId);
public sealed record RestoreNodeSetupRequest(string BackupFileName);

public sealed record NetworkInviteDocument(
    int SchemaVersion,
    string ProtocolVersion,
    string NetworkId,
    string NetworkName,
    string BootstrapNodeId,
    string BootstrapPublicKey,
    string BootstrapFingerprint,
    string BootstrapIrohUrl,
    string BootstrapHttpUrl,
    string BootstrapGrpcUrl,
    IReadOnlyList<string> SupportedRaftTransports,
    string SuggestedRole,
    DateTimeOffset ExpiresAtUtc,
    string Nonce,
    string Signature);

public sealed record InviteValidationResponse(bool Valid, string Message, NetworkInviteDocument? Invite);

public sealed record SetupApplyResponse(
    bool Success,
    string Message,
    IReadOnlyDictionary<string, string> Configuration,
    NetworkInviteDocument? Invite,
    string NextState)
{
    public static SetupApplyResponse Failed(string message) =>
        new(false, message, new Dictionary<string, string>(), null, "ConfigurationError");

    public static SetupApplyResponse Ok(
        IReadOnlyDictionary<string, string> configuration,
        NetworkInviteDocument? invite,
        string nextState) =>
        new(true, "Setup configuration was created. Restart the node to activate it.", configuration, invite, nextState);
}
