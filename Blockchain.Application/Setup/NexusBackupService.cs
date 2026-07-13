using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blockchain.Application.Setup;

public sealed class NexusBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, string> EnvToConfigKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NETWORK_ID"] = "Network:Id",
            ["NETWORK_BOOTSTRAP_HTTP_URL"] = "Network:BootstrapHttpUrl",
            ["NETWORK_BOOTSTRAP_GRPC_URL"] = "Network:BootstrapGrpcUrl",
            ["NETWORK_BOOTSTRAP_IROH_URL"] = "Network:BootstrapIrohUrl",
            ["NETWORK_TRUSTED_BOOTSTRAP_NODE_ID"] = "Network:TrustedBootstrapNodeId",
            ["NETWORK_TRUSTED_BOOTSTRAP_FINGERPRINT"] = "Network:TrustedBootstrapFingerprint",
            ["NODE_DB_PATH"] = "ConnectionStrings:DefaultNodeDb",
            ["NODE_ROLE"] = "Node:Role",
            ["NODE_DB_PASSWORD"] = "NodeDbPassword",
            ["NODE_ADMIN_TOKEN"] = "NodeAdminToken",
            ["WEBHOOK_SECRET"] = "WebhookSecret",
            ["ORACLE_PUBLIC_KEY"] = "OraclePublicKey",
            ["ORACLE_PRIVATE_KEY_PASSWORD"] = "OraclePrivateKeyPassword",
            ["ORACLE_KEY_PATH"] = "OracleKeyPath",
            ["CONSENSUS_ENABLE_POC"] = "Consensus:EnableProofOfContributionValidation",
            ["CONSENSUS_REQUIRE_PROOF_OF_WORK"] = "Consensus:RequireProofOfWork",
            ["CONSENSUS_ACCEPT_P2P_BLOCKS_AS_FINAL"] = "Consensus:AcceptP2PBlocksAsFinal",
            ["CONSENSUS_FINALITY_MODE"] = "Consensus:FinalityMode",
            ["CONSENSUS_PRODUCER_KEY_PATH"] = "Consensus:ProducerKeyPath",
            ["CONSENSUS_PRODUCER_KEY_PASSWORD"] = "Consensus:ProducerPrivateKeyPassword",
            ["RAFT_TRANSPORT"] = "Raft:Transport",
            ["RAFT_NODE_ID"] = "Raft:NodeId",
            ["RAFT_PUBLIC_ENDPOINT"] = "Raft:PublicEndPoint",
            ["RAFT_IROH_NODE_ID"] = "Raft:IrohNodeId",
            ["RAFT_IROH_CONTROL_ENDPOINT"] = "Raft:IrohControlEndPoint",
            ["RAFT_IROH_NODE_LISTEN_ENDPOINT"] = "Raft:IrohNodeListenEndPoint",
            ["RAFT_LOG_PATH"] = "Raft:LogPath",
            ["RAFT_USE_PERSISTENT_MEMBERSHIP"] = "Raft:UsePersistentMembership",
            ["RAFT_MEMBERSHIP_PATH"] = "Raft:MembershipPath",
            ["RAFT_SNAPSHOT_PATH"] = "Raft:SnapshotPath",
            ["RAFT_PEER_ID"] = "Raft:Peers:0:Id",
            ["RAFT_PEER_ENDPOINT"] = "Raft:Peers:0:EndPoint",
            ["P2P_NODE_ID"] = "P2P:NodeId",
            ["P2P_PUBLIC_URL"] = "P2P:PublicUrl",
            ["P2P_BOOTSTRAP_GRPC_URL"] = "P2P:BootstrapPeers:0",
            ["P2P_SYNC_TOKEN"] = "P2P:SyncToken",
            ["P2P_REGISTRATION_TOKEN"] = "P2P:RegistrationToken",
            ["P2P_IDENTITY_KEY_PATH"] = "P2P:IdentityKeyPath",
            ["P2P_DISCOVERY_INTERVAL_SECONDS"] = "P2P:DiscoveryIntervalSeconds",
            ["P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK"] = "P2P:AllowRegistrationTokenFallback",
            ["IROH_ENABLED"] = "P2P:Iroh:Enabled",
            ["IROH_SIDECAR_URL"] = "P2P:Iroh:SidecarUrl",
            ["IROH_LOCAL_API_TOKEN"] = "P2P:Iroh:LocalApiToken",
            ["UPDATE_MANIFEST_URL"] = "NodeVersion:UpdateManifestUrl"
        };

    private static readonly string[] BackupConfigPathKeys =
    [
        "ConnectionStrings:DefaultNodeDb",
        "P2P:IdentityKeyPath",
        "OracleKeyPath",
        "Consensus:ProducerKeyPath",
        "P2P:Iroh:SecretKeyPath"
    ];

    private static readonly string[] PersistedConfigPrefixes =
    [
        "Network:",
        "Node:",
        "ConnectionStrings:",
        "Consensus:",
        "Raft:",
        "P2P:",
        "NodeVersion:"
    ];

    private static readonly string[] PersistedConfigKeys =
    [
        "NodeDbPassword",
        "NodeAdminToken",
        "WebhookSecret",
        "OraclePublicKey",
        "OraclePrivateKeyPassword",
        "OracleKeyPath"
    ];

    public NexusEncryptedBackup CreateBackup(
        IReadOnlyDictionary<string, string?> configuration,
        string dataRoot,
        string applicationVersion,
        string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new InvalidOperationException("Backup password must be at least 12 characters.");
        }

        var values = configuration
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Where(pair => IsPersistedNexusKey(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => NormalizePersistedValue(pair.Key, pair.Value!, dataRoot),
                StringComparer.OrdinalIgnoreCase);

        var files = new List<NexusBackupFile>();
        foreach (string key in BackupConfigPathKeys)
        {
            if (!values.TryGetValue(key, out var configuredPath))
            {
                continue;
            }

            string hostPath = ResolveBackupPath(configuredPath, dataRoot);
            if (!string.IsNullOrWhiteSpace(hostPath) && File.Exists(hostPath))
            {
                files.Add(new NexusBackupFile(key, configuredPath, Convert.ToBase64String(ReadSharedFile(hostPath))));
            }
        }

        var payload = new NexusBackupPayload(
            2,
            applicationVersion,
            DateTime.UtcNow,
            Get(values, "Network:Id"),
            Get(values, "P2P:NodeId"),
            string.Empty,
            values,
            files);

        return EncryptPayload(payload, password);
    }

    public NexusRestoreResult RestoreBackup(
        Stream backupStream,
        string password,
        string setupConfigPath,
        string dataRoot,
        bool force)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return NexusRestoreResult.Failed("Backup password is required.");
        }

        NexusBackupPayload payload;
        try
        {
            var encrypted = JsonSerializer.Deserialize<NexusEncryptedBackup>(backupStream, JsonOptions)
                ?? throw new InvalidOperationException("Backup file is empty.");
            payload = DecryptPayload(encrypted, password);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidOperationException)
        {
            return NexusRestoreResult.Failed($"Backup password is incorrect or backup is damaged: {ex.Message}");
        }

        if (payload.SchemaVersion is < 1 or > 2)
        {
            return NexusRestoreResult.Failed($"Unsupported backup schema version: {payload.SchemaVersion}.");
        }

        var config = payload.ConfigValues?.Count > 0
            ? new Dictionary<string, string>(payload.ConfigValues, StringComparer.OrdinalIgnoreCase)
            : ConvertEnvToConfiguration(payload.EnvContent);

        if (config.Count == 0)
        {
            return NexusRestoreResult.Failed("Backup does not contain restorable configuration.");
        }

        NormalizeRestoredConfigPaths(config, dataRoot);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(setupConfigPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string safetyPath = string.Empty;
        if (File.Exists(setupConfigPath))
        {
            if (!force)
            {
                return NexusRestoreResult.Failed("A node configuration already exists. Confirm restore over existing node before activation.");
            }

            safetyPath = setupConfigPath + $".safety-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(setupConfigPath, safetyPath, overwrite: false);
        }

        try
        {
        foreach (var file in payload.Files)
        {
                string configuredPath = NormalizeConfiguredPath(file.Key, file.ConfiguredPath, config);
                string hostPath = ResolveBackupPath(configuredPath, dataRoot);
                if (string.IsNullOrWhiteSpace(hostPath))
                {
                    continue;
                }

                string? fileDirectory = Path.GetDirectoryName(Path.GetFullPath(hostPath));
                if (!string.IsNullOrWhiteSpace(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                File.WriteAllBytes(hostPath, Convert.FromBase64String(file.ContentBase64));
            }

            File.WriteAllText(
                setupConfigPath,
                JsonSerializer.Serialize(ToNestedJson(config), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(safetyPath) && File.Exists(safetyPath))
            {
                File.Copy(safetyPath, setupConfigPath, overwrite: true);
            }

            throw;
        }

        return new NexusRestoreResult(
            true,
            "Backup restored. Restart the node to activate restored services.",
            payload.NetworkId,
            payload.NodeId,
            FingerprintFromRestoredIdentity(config, dataRoot),
            payload.SchemaVersion,
            safetyPath,
            "AwaitingRestart");
    }

    private static NexusEncryptedBackup EncryptPayload(NexusBackupPayload payload, string password)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
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

    private static NexusBackupPayload DecryptPayload(NexusEncryptedBackup backup, string password)
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

        return JsonSerializer.Deserialize<NexusBackupPayload>(plaintext, JsonOptions)
            ?? throw new InvalidOperationException("Backup payload is empty.");
    }

    private static Dictionary<string, string> ConvertEnvToConfiguration(string? envContent)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(envContent))
        {
            return values;
        }

        foreach (string rawLine in envContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            string key = line[..index].Trim();
            string value = line[(index + 1)..].Trim().Trim('"');
            if (EnvToConfigKeys.TryGetValue(key, out var configKey))
            {
                values[configKey] = value;
            }
        }

        return values;
    }

    private static string NormalizeConfiguredPath(string key, string configuredPath, IReadOnlyDictionary<string, string> config)
    {
        if (config.TryGetValue(key, out var directValue) && !string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return configuredPath;
    }

    private static void NormalizeRestoredConfigPaths(IDictionary<string, string> config, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return;
        }

        string[] keys =
        [
            "ConnectionStrings:DefaultNodeDb",
            "P2P:IdentityKeyPath",
            "OracleKeyPath",
            "Consensus:ProducerKeyPath",
            "P2P:Iroh:SecretKeyPath",
            "Raft:LogPath",
            "Raft:MembershipPath",
            "Raft:SnapshotPath"
        ];

        foreach (string key in keys)
        {
            if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string normalized = value.Replace('\\', '/');
            if (Path.IsPathFullyQualified(value) && !normalized.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            config[key] = ResolveBackupPath(value, dataRoot);
        }
    }

    private static string ResolveBackupPath(string configuredPath, string dataRoot)
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

    private static byte[] ReadSharedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string FingerprintFromRestoredIdentity(IReadOnlyDictionary<string, string> config, string dataRoot)
    {
        if (!config.TryGetValue("P2P:IdentityKeyPath", out var configuredPath))
        {
            return string.Empty;
        }

        string path = ResolveBackupPath(configuredPath, dataRoot);
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(path).Trim()), out _);
            return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())[..8]).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, object?> ToNestedJson(IReadOnlyDictionary<string, string> values)
    {
        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var segments = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (!current.TryGetValue(segments[i], out var child) || child is not Dictionary<string, object?> childDictionary)
                {
                    childDictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    current[segments[i]] = childDictionary;
                }

                current = childDictionary;
            }

            current[segments[^1]] = value;
        }

        return root;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool IsPersistedNexusKey(string key) =>
        PersistedConfigKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
        || PersistedConfigPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string NormalizePersistedValue(string key, string value, string dataRoot)
    {
        if (!IsPathConfigurationKey(key) || string.IsNullOrWhiteSpace(dataRoot))
        {
            return value;
        }

        string fullDataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullValue;
        try
        {
            fullValue = Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }

        if (!fullValue.StartsWith(fullDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        string relative = Path.GetRelativePath(fullDataRoot, fullValue);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsPathConfigurationKey(string key) =>
        key.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ConnectionStrings:DefaultNodeDb", StringComparison.OrdinalIgnoreCase);
}

public sealed record NexusBackupPayload(
    int SchemaVersion,
    string ApplicationVersion,
    DateTime CreatedAtUtc,
    string NetworkId,
    string NodeId,
    string EnvContent,
    IReadOnlyDictionary<string, string>? ConfigValues,
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

public sealed record NexusRestoreResult(
    bool Success,
    string Message,
    string NetworkId,
    string NodeId,
    string Fingerprint,
    int BackupSchemaVersion,
    string SafetyBackupPath,
    string NextState)
{
    public static NexusRestoreResult Failed(string message) =>
        new(false, message, string.Empty, string.Empty, string.Empty, 0, string.Empty, "RecoveryRequired");
}
