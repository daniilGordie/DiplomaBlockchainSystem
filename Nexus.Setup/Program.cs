using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "help";
if (command is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

if (command == "check")
{
    return await CheckNodeAsync(ParseOptions(args.Skip(1)), GetPositional(args, 1, "http://localhost:7042"));
}

if (command == "invite")
{
    return await PrintInviteAsync(ParseOptions(args.Skip(1)), GetPositional(args, 1, "http://localhost:7041"));
}

if (command is "validate" or "validate-env")
{
    return ValidateEnvFile(ParseOptions(args.Skip(1)), GetPositional(args, 1, string.Empty));
}

if (command == "backup")
{
    return CreateBackup(ParseOptions(args.Skip(1)));
}

if (command == "restore")
{
    return RestoreBackup(ParseOptions(args.Skip(1)));
}

if (command != "setup")
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 2;
}

string mode = NormalizeMode(GetPositional(args, 1, "edge"));
if (mode is not ("bootstrap" or "consensus" or "edge" or "local"))
{
    Console.Error.WriteLine("Mode must be bootstrap, consensus, edge, or local.");
    return 2;
}

var options = ParseOptions(args.Skip(2));
string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string deployDir = Path.Combine(repoRoot, "deploy");
string outputPath = GetOption(options, "output")
    ?? Path.Combine(deployDir, GetEnvFileName(mode));
string templatePath = Path.Combine(deployDir, GetTemplateFileName(mode));

if (!File.Exists(templatePath))
{
    Console.Error.WriteLine($"Template not found: {templatePath}");
    return 1;
}

if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 && !HasFlag(options, "force"))
{
    Console.Error.WriteLine($"Refusing to overwrite existing config: {outputPath}");
    Console.Error.WriteLine("Pass --force to overwrite.");
    return 1;
}

var lines = File.ReadAllLines(templatePath).ToList();
NetworkInvite? invite = ParseInvite(GetOption(options, "invite"));
string networkId = GetOption(options, "network-id") ?? invite?.NetworkId ?? (mode == "local" ? "nexus-local" : "nexus-main");
SetValue(lines, "NODE_DB_PASSWORD", NewSecret());
SetValue(lines, "NODE_ADMIN_TOKEN", NewSecret());
SetValue(lines, "WEBHOOK_SECRET", NewSecret());
SetValue(lines, "ORACLE_PRIVATE_KEY_PASSWORD", NewSecret());
SetValue(lines, "CONSENSUS_PRODUCER_KEY_PASSWORD", NewSecret());
SetValue(lines, "P2P_REGISTRATION_TOKEN", string.Empty);
SetValue(lines, "P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK", "false");
SetValue(lines, "NETWORK_ID", networkId);

string nodeId = GetOption(options, "node-id") ?? $"{mode}-{Guid.NewGuid():N}"[..Math.Min(mode.Length + 9, mode.Length + 33)];
SetValue(lines, "NODE_ROLE", ToNodeRole(mode));
SetValue(lines, "P2P_NODE_ID", nodeId);

if (mode == "bootstrap")
{
    string? publicUrl = GetOption(options, "public-url");
    if (!string.IsNullOrWhiteSpace(publicUrl))
    {
        string normalizedPublicUrl = NormalizeUrl(publicUrl);
        string normalizedGrpcUrl = NormalizeUrl(GetOption(options, "grpc-url") ?? DeriveUrlWithPort(normalizedPublicUrl, 7141));
        SetValue(lines, "P2P_PUBLIC_URL", normalizedPublicUrl);
        SetValue(lines, "NETWORK_BOOTSTRAP_HTTP_URL", normalizedPublicUrl);
        SetValue(lines, "NETWORK_BOOTSTRAP_GRPC_URL", normalizedGrpcUrl);
        SetValue(lines, "RAFT_TRANSPORT", "Tcp");
        SetValue(lines, "RAFT_NODE_ID", nodeId);
        SetValue(lines, "RAFT_PUBLIC_ENDPOINT", GetOption(options, "raft-endpoint") ?? DeriveHostPort(normalizedPublicUrl, 6041));
        SetValue(lines, "RAFT_PEER_ID", GetOption(options, "raft-peer-id") ?? string.Empty);
        SetValue(lines, "RAFT_PEER_ENDPOINT", GetOption(options, "raft-peer-endpoint") ?? string.Empty);
        SetValue(lines, "NETWORK_TRUSTED_BOOTSTRAP_NODE_ID", nodeId);
    }
}
else if (mode is "edge" or "consensus")
{
    SetValue(lines, "IROH_LOCAL_API_TOKEN", NewSecret());
    string? bootstrapGrpcUrl = GetOption(options, "bootstrap") ?? invite?.BootstrapGrpcUrl;
    if (!string.IsNullOrWhiteSpace(bootstrapGrpcUrl))
    {
        SetValue(lines, "P2P_BOOTSTRAP_GRPC_URL", NormalizeUrl(bootstrapGrpcUrl));
        SetValue(lines, "NETWORK_BOOTSTRAP_GRPC_URL", NormalizeUrl(bootstrapGrpcUrl));
    }

    SetValue(lines, "NETWORK_BOOTSTRAP_HTTP_URL", invite?.BootstrapHttpUrl ?? string.Empty);
    string? bootstrapHttpUrl = GetOption(options, "bootstrap-http");
    if (!string.IsNullOrWhiteSpace(bootstrapHttpUrl))
    {
        SetValue(lines, "NETWORK_BOOTSTRAP_HTTP_URL", NormalizeUrl(bootstrapHttpUrl));
    }

    SetValue(lines, "NETWORK_BOOTSTRAP_IROH_URL", invite?.BootstrapIrohUrl ?? string.Empty);
    SetValue(lines, "NETWORK_TRUSTED_BOOTSTRAP_NODE_ID", invite?.TrustedBootstrapNodeId ?? string.Empty);
    SetValue(lines, "NETWORK_TRUSTED_BOOTSTRAP_FINGERPRINT", invite?.TrustedBootstrapFingerprint ?? string.Empty);
}

string? oraclePublicKey = GetOption(options, "oracle-public-key");
if (!string.IsNullOrWhiteSpace(oraclePublicKey))
{
    SetValue(lines, "ORACLE_PUBLIC_KEY", oraclePublicKey.Trim());
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllLines(outputPath, lines);

Console.WriteLine($"Created {outputPath}");
Console.WriteLine("Review public URLs and ORACLE_PUBLIC_KEY before starting Docker Compose.");
Console.WriteLine($"Start: {GetStartCommand(mode)}");
return 0;

static void PrintHelp()
{
    Console.WriteLine("""
Nexus.Setup

Usage:
  dotnet run --project Nexus.Setup -- setup bootstrap --public-url https://bootstrap.example.com
  dotnet run --project Nexus.Setup -- setup edge --bootstrap https://bootstrap.example.com:7141
  dotnet run --project Nexus.Setup -- setup edge --invite <invite-token>
  dotnet run --project Nexus.Setup -- setup consensus --bootstrap https://bootstrap.example.com:7141
  dotnet run --project Nexus.Setup -- setup local
  dotnet run --project Nexus.Setup -- invite --url https://bootstrap.example.com
  dotnet run --project Nexus.Setup -- check --url http://localhost:7042
  dotnet run --project Nexus.Setup -- validate --env deploy/edge-node.env
  dotnet run --project Nexus.Setup -- backup --env deploy/edge-node.env --data-root ./.tmp/edge-data --output backups/edge.nexus-backup --password <backup-password>
  dotnet run --project Nexus.Setup -- restore --input backups/edge.nexus-backup --data-root ./.tmp/restored-edge-data --env-output deploy/restored-edge.env --password <backup-password>

Options:
  --output <path>             Output .env path.
  --force                     Overwrite existing output file.
  --node-id <id>              Stable node id to write.
  --network-id <id>           Network id to write when no invite is used.
  --public-url <url>          Bootstrap public URL.
  --grpc-url <url>            Bootstrap gRPC URL advertised in invites.
  --raft-endpoint <host:port> Bootstrap/consensus Raft public endpoint.
  --raft-peer-id <id>         Optional initial remote Raft peer id.
  --raft-peer-endpoint <ep>   Optional initial remote Raft peer endpoint.
  --bootstrap <url>           Bootstrap gRPC URL for edge or consensus nodes.
  --bootstrap-http <url>      Bootstrap HTTP URL override for edge sync.
  --invite <token-or-json>    Connection invite from /api/network/invite.
  --oracle-public-key <key>   Oracle public key value.
  --url <url>                 Node HTTP URL for invite/check commands.
  --env <path>                Env file path for validate command.
  --data-root <path>          Host data root used to resolve /data key paths for backup/restore.
  --input <path>              Backup input path for restore.
  --password <value>          Backup password. Prefer passing via automation secret store.
""");
}

static async Task<int> CheckNodeAsync(IReadOnlyDictionary<string, string?> options, string fallbackUrl)
{
    string nodeUrl = NormalizeNodeUrl(GetOption(options, "url") ?? fallbackUrl);
    using var http = new HttpClient { BaseAddress = new Uri(nodeUrl), Timeout = TimeSpan.FromSeconds(10) };

    bool healthOk = false;
    JsonDocument? diagnostics = null;
    JsonDocument? network = null;
    try
    {
        using var healthResponse = await http.GetAsync("/healthz");
        healthOk = healthResponse.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"healthz failed: {ex.Message}");
    }

    try
    {
        diagnostics = await http.GetFromJsonAsync<JsonDocument>("/api/setup/diagnostics");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"setup diagnostics failed: {ex.Message}");
    }

    try
    {
        network = await http.GetFromJsonAsync<JsonDocument>("/api/network/status");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"network status failed: {ex.Message}");
    }

    bool ready = diagnostics != null && GetBool(diagnostics.RootElement, "ready");
    Console.WriteLine($"Node: {nodeUrl}");
    Console.WriteLine($"Health: {(healthOk ? "ok" : "failed")}");
    Console.WriteLine($"Diagnostics: {(ready ? "ready" : "not ready")}");

    if (network != null)
    {
        var root = network.RootElement;
        Console.WriteLine($"Role: {GetString(root, "nodeRole")}");
        Console.WriteLine($"Network: {GetString(root, "networkId")}");
        string raftTransport = GetString(root, "raftTransport");
        Console.WriteLine($"Consensus: {GetString(root, "finalityMode")}, raft member={GetBool(root, "runsRaft")}, raft transport={(string.IsNullOrWhiteSpace(raftTransport) ? "Tcp" : raftTransport)}");
        Console.WriteLine($"Iroh: enabled={GetBool(root, "irohEnabled")}, healthy={GetBool(root, "irohSidecarHealthy")}");
        Console.WriteLine($"Peers: {GetArrayLength(root, "knownPeers")}");
        Console.WriteLine($"Channels: {GetArrayLength(root, "channels")}");
        if (TryGetProperty(root, "edgeSync", out var edgeSync))
        {
            Console.WriteLine($"Edge sync: {GetString(edgeSync, "state")}");
        }

        if (TryGetProperty(root, "edgeProposalForwarding", out var forwarding))
        {
            Console.WriteLine($"Proposal forwarding: {GetString(forwarding, "state")}, pending={GetInt(forwarding, "pendingCount")}, ok={GetLong(forwarding, "successCount")}, failed={GetLong(forwarding, "failureCount")}");
            string forwardingMessage = GetString(forwarding, "lastMessage");
            if (!string.IsNullOrWhiteSpace(forwardingMessage))
            {
                Console.WriteLine($"Forwarding message: {forwardingMessage}");
            }
        }
    }

    PrintStringArray(diagnostics?.RootElement, "errors", "Errors");
    PrintStringArray(diagnostics?.RootElement, "warnings", "Warnings");

    diagnostics?.Dispose();
    network?.Dispose();
    return healthOk && ready ? 0 : 1;
}

static async Task<int> PrintInviteAsync(IReadOnlyDictionary<string, string?> options, string fallbackUrl)
{
    string nodeUrl = NormalizeNodeUrl(GetOption(options, "url") ?? fallbackUrl);
    using var http = new HttpClient { BaseAddress = new Uri(nodeUrl), Timeout = TimeSpan.FromSeconds(10) };

    try
    {
        using var invite = await http.GetFromJsonAsync<JsonDocument>("/api/network/invite");
        if (invite == null)
        {
            Console.Error.WriteLine("Invite endpoint returned no data.");
            return 1;
        }

        var root = invite.RootElement;
        Console.WriteLine($"Network: {GetString(root, "networkId")}");
        Console.WriteLine($"Bootstrap HTTP: {GetString(root, "bootstrapHttpUrl")}");
        Console.WriteLine($"Bootstrap gRPC: {GetString(root, "bootstrapGrpcUrl")}");
        Console.WriteLine($"Bootstrap Iroh: {GetString(root, "bootstrapIrohUrl")}");
        Console.WriteLine($"Trusted node: {GetString(root, "trustedBootstrapNodeId")}");
        Console.WriteLine($"Fingerprint: {GetString(root, "trustedBootstrapFingerprint")}");
        Console.WriteLine("Token:");
        Console.WriteLine(GetString(root, "token"));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Invite request failed: {ex.Message}");
        return 1;
    }
}

static int ValidateEnvFile(IReadOnlyDictionary<string, string?> options, string fallbackPath)
{
    string path = GetOption(options, "env") ?? GetOption(options, "file") ?? fallbackPath;
    if (string.IsNullOrWhiteSpace(path))
    {
        Console.Error.WriteLine("Pass --env <path>.");
        return 2;
    }

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Env file not found: {path}");
        return 2;
    }

    var values = LoadEnvFile(path, out var parseWarnings);
    var errors = new List<string>();
    var warnings = new List<string>(parseWarnings);
    string role = GetEnv(values, "NODE_ROLE", "Edge");
    bool localRole = IsRole(role, "Local");
    var criticalSecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "NODE_DB_PASSWORD",
        "NODE_ADMIN_TOKEN",
        "WEBHOOK_SECRET",
        "ORACLE_PRIVATE_KEY_PASSWORD",
        "CONSENSUS_PRODUCER_KEY_PASSWORD",
        "IROH_LOCAL_API_TOKEN"
    };

    RequireValue(values, "NODE_ROLE", errors);
    RequireSecret(values, "NODE_DB_PASSWORD", errors, warnings);
    RequireSecret(values, "NODE_ADMIN_TOKEN", errors, warnings);
    RequireSecret(values, "WEBHOOK_SECRET", errors, warnings);
    RequireSecret(values, "ORACLE_PRIVATE_KEY_PASSWORD", errors, warnings);
    RequireSecret(values, "CONSENSUS_PRODUCER_KEY_PASSWORD", errors, warnings);
    RequireNoPlaceholder(values, "NETWORK_ID", warnings);

    if (IsRole(role, "Edge"))
    {
        RequireValue(values, "P2P_BOOTSTRAP_GRPC_URL", errors);
        RequireSecret(values, "IROH_LOCAL_API_TOKEN", errors, warnings);
        if (string.IsNullOrWhiteSpace(GetEnv(values, "NETWORK_TRUSTED_BOOTSTRAP_FINGERPRINT")))
        {
            warnings.Add("NETWORK_TRUSTED_BOOTSTRAP_FINGERPRINT is empty. Prefer generating Edge config from a bootstrap invite.");
        }
    }

    if (IsRole(role, "Bootstrap") || IsRole(role, "Consensus"))
    {
        string raftTransport = GetEnv(values, "RAFT_TRANSPORT", "Tcp");
        bool tcpTransport = string.Equals(raftTransport, "Tcp", StringComparison.OrdinalIgnoreCase);
        bool irohTransport = string.Equals(raftTransport, "Iroh", StringComparison.OrdinalIgnoreCase);
        if (!tcpTransport && !irohTransport)
        {
            errors.Add("RAFT_TRANSPORT must be Tcp or Iroh.");
        }

        RequireValue(values, "RAFT_NODE_ID", errors);
        if (tcpTransport)
        {
            RequireValue(values, "RAFT_PUBLIC_ENDPOINT", errors);
        }
        else if (irohTransport)
        {
            RequireValue(values, "RAFT_IROH_NODE_ID", errors);
            RequireSecret(values, "IROH_LOCAL_API_TOKEN", errors, warnings);
            RequireValue(values, "IROH_RAFT_LISTEN", errors);
            RequireValue(values, "IROH_RAFT_NODE_LISTEN", errors);
        }
        if (string.IsNullOrWhiteSpace(GetEnv(values, "RAFT_PEER_ENDPOINT")))
        {
            warnings.Add("RAFT_PEER_ENDPOINT is empty. This node will start as a single-member Raft cluster without fault tolerance.");
        }

        if (irohTransport &&
            !string.IsNullOrWhiteSpace(GetEnv(values, "RAFT_PEER_ENDPOINT")) &&
            !GetEnv(values, "RAFT_PEER_ENDPOINT").StartsWith("iroh://", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("RAFT_PEER_ENDPOINT must use iroh://<node-id> when RAFT_TRANSPORT=Iroh.");
        }

        if (!string.IsNullOrWhiteSpace(GetEnv(values, "RAFT_PEER_ID")) &&
            string.IsNullOrWhiteSpace(GetEnv(values, "RAFT_PEER_ENDPOINT")))
        {
            errors.Add("RAFT_PEER_ENDPOINT is required when RAFT_PEER_ID is configured.");
        }

        if (string.IsNullOrWhiteSpace(GetEnv(values, "RAFT_PEER_ID")) &&
            !string.IsNullOrWhiteSpace(GetEnv(values, "RAFT_PEER_ENDPOINT")))
        {
            errors.Add("RAFT_PEER_ID is required when RAFT_PEER_ENDPOINT is configured.");
        }
    }

    if (localRole)
    {
        if (!string.IsNullOrWhiteSpace(GetEnv(values, "P2P_BOOTSTRAP_GRPC_URL")))
        {
            warnings.Add("Local node has P2P_BOOTSTRAP_GRPC_URL configured; it will not run as an isolated private node.");
        }
    }
    foreach (var (key, value) in values)
    {
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"{key} has leading or trailing whitespace.");
        }

        if (!criticalSecretKeys.Contains(key) && HasPlaceholder(value))
        {
            warnings.Add($"{key} contains a placeholder value.");
        }
    }

    Console.WriteLine($"Env: {path}");
    Console.WriteLine($"Role: {role}");
    Console.WriteLine($"Consensus: {(localRole ? "local PoC validation" : "Proof of Contribution + DotNext Raft")}");
    PrintList("Errors", errors);
    PrintList("Warnings", warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    Console.WriteLine(errors.Count == 0 ? "Result: ready" : "Result: not ready");
    return errors.Count == 0 ? 0 : 1;
}

static int CreateBackup(IReadOnlyDictionary<string, string?> options)
{
    string envPath = GetOption(options, "env") ?? string.Empty;
    string outputPath = GetOption(options, "output") ?? string.Empty;
    string password = GetOption(options, "password") ?? string.Empty;
    string dataRoot = GetOption(options, "data-root") ?? string.Empty;

    if (string.IsNullOrWhiteSpace(envPath) || !File.Exists(envPath))
    {
        Console.Error.WriteLine("Backup requires --env <existing-env-file>.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        Console.Error.WriteLine("Backup requires --output <backup-file>.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
    {
        Console.Error.WriteLine("Backup password must be at least 12 characters.");
        return 2;
    }

    var values = LoadEnvFile(envPath, out _);
    var files = new List<NexusBackupFile>();
    foreach (string key in NexusSetupJson.BackupPathKeys)
    {
        string configuredPath = GetEnv(values, key, string.Empty);
        string hostPath = ResolveBackupPath(configuredPath, dataRoot);
        if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
        {
            files.Add(new NexusBackupFile(key, configuredPath, Convert.ToBase64String(File.ReadAllBytes(hostPath))));
        }
    }

    var payload = new NexusBackupPayload(
        1,
        "1.0.0",
        DateTime.UtcNow,
        GetEnv(values, "NETWORK_ID", "nexus-main"),
        GetEnv(values, "P2P_NODE_ID", ""),
        File.ReadAllText(envPath),
        files);
    byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, NexusSetupJson.Options);
    var encrypted = EncryptBackupPayload(plaintext, password);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
    File.WriteAllText(outputPath, JsonSerializer.Serialize(encrypted, NexusSetupJson.Options));
    Console.WriteLine($"Created encrypted backup: {outputPath}");
    Console.WriteLine($"Network: {payload.NetworkId}");
    Console.WriteLine($"Node: {payload.NodeId}");
    Console.WriteLine($"Files: {files.Count}");
    return 0;
}

static int RestoreBackup(IReadOnlyDictionary<string, string?> options)
{
    string inputPath = GetOption(options, "input") ?? string.Empty;
    string envOutputPath = GetOption(options, "env-output") ?? GetOption(options, "env") ?? string.Empty;
    string password = GetOption(options, "password") ?? string.Empty;
    string dataRoot = GetOption(options, "data-root") ?? string.Empty;
    bool force = HasFlag(options, "force");

    if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
    {
        Console.Error.WriteLine("Restore requires --input <backup-file>.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(envOutputPath))
    {
        Console.Error.WriteLine("Restore requires --env-output <env-file>.");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(password))
    {
        Console.Error.WriteLine("Restore requires --password <backup-password>.");
        return 2;
    }

    if (File.Exists(envOutputPath) && !force)
    {
        Console.Error.WriteLine($"Refusing to overwrite existing env: {envOutputPath}. Pass --force to replace it.");
        return 1;
    }

    NexusEncryptedBackup encrypted;
    try
    {
        encrypted = JsonSerializer.Deserialize<NexusEncryptedBackup>(File.ReadAllText(inputPath), NexusSetupJson.Options)
            ?? throw new InvalidOperationException("Backup file is empty.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Backup format is invalid: {ex.Message}");
        return 1;
    }

    NexusBackupPayload payload;
    try
    {
        byte[] plaintext = DecryptBackupPayload(encrypted, password);
        payload = JsonSerializer.Deserialize<NexusBackupPayload>(plaintext, NexusSetupJson.Options)
            ?? throw new InvalidOperationException("Backup payload is empty.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Backup password is incorrect or backup is damaged: {ex.Message}");
        return 1;
    }

    if (payload.SchemaVersion != 1)
    {
        Console.Error.WriteLine($"Unsupported backup schema version: {payload.SchemaVersion}");
        return 1;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(envOutputPath)) ?? ".");
    File.WriteAllText(envOutputPath, payload.EnvContent);
    foreach (var file in payload.Files)
    {
        string hostPath = ResolveBackupPath(file.ConfiguredPath, dataRoot);
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            continue;
        }

        if (File.Exists(hostPath) && !force)
        {
            Console.Error.WriteLine($"Refusing to overwrite existing file: {hostPath}. Pass --force to replace it.");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hostPath)) ?? ".");
        File.WriteAllBytes(hostPath, Convert.FromBase64String(file.ContentBase64));
    }

    Console.WriteLine($"Restored env: {envOutputPath}");
    Console.WriteLine($"Network: {payload.NetworkId}");
    Console.WriteLine($"Node: {payload.NodeId}");
    Console.WriteLine($"Files: {payload.Files.Count}");
    return 0;
}

static NexusEncryptedBackup EncryptBackupPayload(byte[] plaintext, string password)
{
    byte[] salt = RandomNumberGenerator.GetBytes(16);
    byte[] nonce = RandomNumberGenerator.GetBytes(12);
    byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
    byte[] ciphertext = new byte[plaintext.Length];
    byte[] tag = new byte[16];
    using var aes = new AesGcm(key, 16);
    aes.Encrypt(nonce, plaintext, ciphertext, tag);
    CryptographicOperations.ZeroMemory(key);
    return new NexusEncryptedBackup(
        1,
        "PBKDF2-SHA256",
        210_000,
        "AES-256-GCM",
        Convert.ToBase64String(salt),
        Convert.ToBase64String(nonce),
        Convert.ToBase64String(tag),
        Convert.ToBase64String(ciphertext));
}

static byte[] DecryptBackupPayload(NexusEncryptedBackup backup, string password)
{
    byte[] salt = Convert.FromBase64String(backup.SaltBase64);
    byte[] nonce = Convert.FromBase64String(backup.NonceBase64);
    byte[] tag = Convert.FromBase64String(backup.TagBase64);
    byte[] ciphertext = Convert.FromBase64String(backup.CiphertextBase64);
    byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, backup.Iterations, HashAlgorithmName.SHA256, 32);
    byte[] plaintext = new byte[ciphertext.Length];
    using var aes = new AesGcm(key, 16);
    aes.Decrypt(nonce, ciphertext, tag, plaintext);
    CryptographicOperations.ZeroMemory(key);
    return plaintext;
}

static string ResolveBackupPath(string configuredPath, string dataRoot)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return string.Empty;
    }

    string normalized = configuredPath.Replace('\\', '/');
    if (Path.IsPathFullyQualified(configuredPath) && !normalized.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
    {
        return configuredPath;
    }

    if (normalized.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return string.Empty;
        }

        return Path.Combine(dataRoot, normalized["/data/".Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    return string.IsNullOrWhiteSpace(dataRoot)
        ? configuredPath
        : Path.Combine(dataRoot, configuredPath.Replace('/', Path.DirectorySeparatorChar));
}

static string NormalizeMode(string mode) =>
    mode.Trim().ToLowerInvariant() switch
    {
        "boot" or "bootstrap-node" => "bootstrap",
        "raft" or "raft-member" or "consensus-node" => "consensus",
        "join" or "full" or "fullnode" or "full-node" or "edge-node" => "edge",
        "private" or "local-node" => "local",
        var value => value
    };

static string ToNodeRole(string mode) =>
    mode switch
    {
        "bootstrap" => "Bootstrap",
        "consensus" => "Consensus",
        "local" => "Local",
        _ => "Edge"
    };

static string GetEnvFileName(string mode) =>
    mode switch
    {
        "bootstrap" => "bootstrap-node.env",
        "consensus" => "consensus-node.env",
        "local" => "local-node.env",
        _ => "edge-node.env"
    };

static string GetTemplateFileName(string mode) =>
    mode switch
    {
        "bootstrap" => "bootstrap-node.env.example",
        "consensus" => "full-node.env.example",
        "local" => "local-node.env.example",
        _ => "edge-node.env.example"
    };

static string GetStartCommand(string mode) =>
    mode switch
    {
        "bootstrap" => "docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up -d --build",
        "consensus" => "docker compose --env-file deploy/consensus-node.env -f deploy/docker-compose.full-node.yml up -d --build",
        "local" => "docker compose --env-file deploy/local-node.env -f deploy/docker-compose.local-node.yml up -d --build",
        _ => "docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml up -d --build"
    };

static Dictionary<string, string?> ParseOptions(IEnumerable<string> tokens)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    string? pending = null;
    foreach (string token in tokens)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            pending = token[2..];
            result[pending] = "true";
            continue;
        }

        if (pending != null)
        {
            result[pending] = token;
            pending = null;
        }
    }

    return result;
}

static string? GetOption(IReadOnlyDictionary<string, string?> options, string name) =>
    options.TryGetValue(name, out var value) && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        ? value
        : null;

static bool HasFlag(IReadOnlyDictionary<string, string?> options, string name) =>
    options.TryGetValue(name, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

static string GetPositional(string[] args, int index, string fallback) => args.Length > index ? args[index] : fallback;

static Dictionary<string, string> LoadEnvFile(string path, out List<string> warnings)
{
    warnings = new List<string>();
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    int lineNumber = 0;
    foreach (string rawLine in File.ReadLines(path))
    {
        lineNumber++;
        string line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            warnings.Add($"Line {lineNumber} is not KEY=value.");
            continue;
        }

        values[line[..separator].Trim()] = line[(separator + 1)..];
    }

    return values;
}

static string GetEnv(IReadOnlyDictionary<string, string> values, string key, string fallback = "") =>
    values.TryGetValue(key, out string? value) ? value : fallback;

static bool IsRole(string role, string expected) =>
    string.Equals(role, expected, StringComparison.OrdinalIgnoreCase);

static void RequireValue(IReadOnlyDictionary<string, string> values, string key, List<string> errors)
{
    if (string.IsNullOrWhiteSpace(GetEnv(values, key)))
    {
        errors.Add($"{key} is required.");
    }
}

static void RequireSecret(IReadOnlyDictionary<string, string> values, string key, List<string> errors, List<string> warnings)
{
    string value = GetEnv(values, key);
    if (string.IsNullOrWhiteSpace(value))
    {
        errors.Add($"{key} is required.");
        return;
    }

    if (HasPlaceholder(value))
    {
        errors.Add($"{key} still contains a placeholder.");
    }
}

static void RequireNoPlaceholder(IReadOnlyDictionary<string, string> values, string key, List<string> warnings)
{
    string value = GetEnv(values, key);
    if (!string.IsNullOrWhiteSpace(value) && HasPlaceholder(value))
    {
        warnings.Add($"{key} still contains a placeholder.");
    }
}

static bool HasPlaceholder(string value) =>
    value.Contains("change-this", StringComparison.OrdinalIgnoreCase) ||
    value.Contains("FULL_NODE_PUBLIC_IP_OR_DOMAIN", StringComparison.OrdinalIgnoreCase);

static void PrintList(string label, IReadOnlyList<string> items)
{
    if (items.Count == 0)
    {
        return;
    }

    Console.WriteLine($"{label}:");
    foreach (string item in items)
    {
        Console.WriteLine($"  - {item}");
    }
}

static string NewSecret()
{
    Span<byte> buffer = stackalloc byte[32];
    RandomNumberGenerator.Fill(buffer);
    return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static void SetValue(List<string> lines, string name, string value)
{
    for (int i = 0; i < lines.Count; i++)
    {
        if (lines[i].StartsWith($"{name}=", StringComparison.Ordinal))
        {
            lines[i] = $"{name}={value}";
            return;
        }
    }

    lines.Add($"{name}={value}");
}

static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');

static string NormalizeNodeUrl(string value) => NormalizeUrl(value) + "/";

static string DeriveUrlWithPort(string url, int port)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return url;
    }

    var builder = new UriBuilder(uri)
    {
        Port = port,
        Path = string.Empty,
        Query = string.Empty,
        Fragment = string.Empty
    };
    return builder.Uri.ToString().TrimEnd('/');
}

static string DeriveHostPort(string url, int port)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return url;
    }

    string host = uri.Host.Contains(":", StringComparison.Ordinal) && !uri.Host.StartsWith("[", StringComparison.Ordinal)
        ? $"[{uri.Host}]"
        : uri.Host;
    return $"{host}:{port}";
}

static bool TryGetProperty(JsonElement root, string camelName, out JsonElement value)
{
    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(camelName, out value))
    {
        return true;
    }

    string pascalName = char.ToUpperInvariant(camelName[0]) + camelName[1..];
    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(pascalName, out value))
    {
        return true;
    }

    value = default;
    return false;
}

static string GetString(JsonElement root, string name) =>
    TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : string.Empty;

static bool GetBool(JsonElement root, string name) =>
    TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.True;

static int GetArrayLength(JsonElement root, string name) =>
    TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.GetArrayLength()
        : 0;

static int GetInt(JsonElement root, string name) =>
    TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
        ? result
        : 0;

static long GetLong(JsonElement root, string name) =>
    TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
        ? result
        : 0;

static void PrintStringArray(JsonElement? root, string name, string label)
{
    if (root == null || !TryGetProperty(root.Value, name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
    {
        return;
    }

    Console.WriteLine($"{label}:");
    foreach (var item in array.EnumerateArray())
    {
        Console.WriteLine($"  - {item.GetString()}");
    }
}

static NetworkInvite? ParseInvite(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    try
    {
        string trimmed = value.Trim();
        string json = trimmed.StartsWith('{')
            ? trimmed
            : Encoding.UTF8.GetString(DecodeBase64Url(trimmed));
        return JsonSerializer.Deserialize<NetworkInvite>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
    catch
    {
        Console.Error.WriteLine("Warning: connection invite could not be parsed; falling back to explicit options.");
        return null;
    }
}

static byte[] DecodeBase64Url(string value)
{
    string padded = value.Replace('-', '+').Replace('_', '/');
    int padding = padded.Length % 4;
    if (padding > 0)
    {
        padded = padded.PadRight(padded.Length + 4 - padding, '=');
    }

    return Convert.FromBase64String(padded);
}

static string FindRepoRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory != null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DiplomaBlockchainSystem.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

public sealed record NetworkInvite(
    string NetworkId,
    string BootstrapHttpUrl,
    string BootstrapGrpcUrl,
    string BootstrapIrohUrl,
    string TrustedBootstrapNodeId,
    string TrustedBootstrapRole,
    string TrustedBootstrapFingerprint,
    string SuggestedRole,
    DateTime CreatedAtUtc,
    string Token);

public sealed record NexusBackupPayload(
    int SchemaVersion,
    string ApplicationVersion,
    DateTime CreatedAtUtc,
    string NetworkId,
    string NodeId,
    string EnvContent,
    IReadOnlyList<NexusBackupFile> Files);

public sealed record NexusBackupFile(
    string Key,
    string ConfiguredPath,
    string ContentBase64);

public sealed record NexusEncryptedBackup(
    int SchemaVersion,
    string Kdf,
    int Iterations,
    string Encryption,
    string SaltBase64,
    string NonceBase64,
    string TagBase64,
    string CiphertextBase64);

static partial class NexusSetupJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static readonly string[] BackupPathKeys =
    [
        "P2P_IDENTITY_KEY_PATH",
        "ORACLE_KEY_PATH",
        "CONSENSUS_PRODUCER_KEY_PATH",
        "IROH_SECRET_KEY_PATH"
    ];
}
