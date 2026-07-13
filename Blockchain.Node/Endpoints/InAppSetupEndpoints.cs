using Blockchain.Application.Setup;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blockchain.Node.Endpoints;

public static class InAppSetupEndpoints
{
    public static IEndpointRouteBuilder MapInAppSetupEndpoints(this IEndpointRouteBuilder endpoints, bool fullNodeConfigured)
    {
        endpoints.MapGet("/api/setup/state", (
            IConfiguration configuration,
            NexusSetupUseCases setup) =>
        {
            return Results.Json(setup.GetState(ToDictionary(configuration), fullNodeConfigured));
        });

        endpoints.MapGet("/api/setup/capabilities", () => Results.Json(new
        {
            modes = new[] { "Create network", "Join network", "Local private node", "Restore node" },
            raftTransports = new[] { "Tcp", "Iroh" },
            setupStateMachine = new[] { "NotConfigured", "DetectingExistingNode", "Configuring", "AwaitingRestart", "Ready", "ConfigurationError", "RecoveryRequired" }
        }));

        endpoints.MapPost("/api/setup/validate-invite", (
            InviteValidationRequest request,
            NexusSetupUseCases setup) =>
        {
            return Results.Json(setup.ValidateInvite(request.Invite));
        });

        endpoints.MapPost("/api/setup/create-network", async (
            CreateNetworkSetupRequest request,
            IConfiguration configuration,
            NexusSetupUseCases setup,
            CancellationToken cancellationToken) =>
        {
            var result = setup.CreateNetwork(request);
            return await PersistResultAsync(result, configuration, setup, cancellationToken);
        });

        endpoints.MapPost("/api/setup/join-network", async (
            JoinNetworkSetupRequest request,
            IConfiguration configuration,
            NexusSetupUseCases setup,
            CancellationToken cancellationToken) =>
        {
            var result = setup.JoinNetwork(request);
            return await PersistResultAsync(result, configuration, setup, cancellationToken);
        });

        endpoints.MapPost("/api/setup/local", async (
            LocalNodeSetupRequest request,
            IConfiguration configuration,
            NexusSetupUseCases setup,
            CancellationToken cancellationToken) =>
        {
            var result = setup.CreateLocalNode(request);
            return await PersistResultAsync(result, configuration, setup, cancellationToken);
        });

        endpoints.MapPost("/api/setup/restore", async (
            HttpRequest request,
            IConfiguration configuration,
            NexusBackupService backupService,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { success = false, message = "Restore requires multipart/form-data." });
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("backup");
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { success = false, message = "Backup file is required." });
            }

            string password = form["password"].ToString();
            bool force = bool.TryParse(form["force"].ToString(), out bool parsedForce) && parsedForce;
            await using var input = file.OpenReadStream();
            var result = backupService.RestoreBackup(
                input,
                password,
                GetSetupConfigPath(configuration),
                GetSetupDataDirectory(configuration),
                force);

            return result.Success ? Results.Json(result) : Results.BadRequest(result);
        });

        endpoints.MapPost("/api/setup/backup", async (
            HttpRequest request,
            IConfiguration configuration,
            NexusBackupService backupService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                string password;
                if (request.HasFormContentType)
                {
                    var form = await request.ReadFormAsync(cancellationToken);
                    password = form["password"].ToString();
                }
                else
                {
                    var body = await JsonSerializer.DeserializeAsync<SetupBackupRequest>(
                        request.Body,
                        cancellationToken: cancellationToken);
                    password = body?.Password ?? string.Empty;
                }

                var backup = backupService.CreateBackup(
                    ToDictionary(configuration),
                    GetSetupDataDirectory(configuration),
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
                    password);

                return Results.File(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true })),
                    "application/json",
                    $"nexus-{DateTime.UtcNow:yyyyMMddHHmmss}.nexus-backup");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        endpoints.MapPost("/api/setup/generate-invite", (
            CreateNetworkSetupRequest request,
            IConfiguration configuration,
            NexusSetupUseCases setup) =>
        {
            var result = setup.CreateNetwork(request);
            if (result.Success && result.Invite != null)
            {
                var persistedConfiguration = NormalizeLocalPaths(configuration, result.Configuration);
                result = result with { Invite = SignInvite(persistedConfiguration, result.Invite) };
            }

            return result.Success
                ? Results.Json(result.Invite)
                : Results.BadRequest(result);
        });

        endpoints.MapGet("/api/setup/join-status", (
            IConfiguration configuration) =>
        {
            return Results.Json(new
            {
                status = configuration["Setup:JoinStatus"] ?? (fullNodeConfigured ? "Connected" : "NotConfigured"),
                membership = configuration["Setup:MembershipStatus"] ?? string.Empty
            });
        });

        endpoints.MapPost("/api/setup/restart", () =>
        {
            return Results.Json(new
            {
                success = false,
                message = "Restart must be performed by the process supervisor or container runtime.",
                restartRequired = true
            });
        });

        return endpoints;
    }

    private static async Task<IResult> PersistResultAsync(
        SetupApplyResponse result,
        IConfiguration configuration,
        NexusSetupUseCases setup,
        CancellationToken cancellationToken)
    {
        if (!result.Success)
        {
            return Results.BadRequest(result);
        }

        var persistedConfiguration = NormalizeLocalPaths(configuration, result.Configuration);
        var persistedResult = result.Invite == null
            ? result
            : result with { Invite = SignInvite(persistedConfiguration, result.Invite) };

        await WriteSetupStateAsync(configuration, persistedConfiguration, cancellationToken);
        return Results.Json(persistedResult);
    }

    private static IReadOnlyDictionary<string, string> NormalizeLocalPaths(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string> values)
    {
        var normalized = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        string dataDirectory = GetSetupDataDirectory(configuration);
        Directory.CreateDirectory(dataDirectory);

        if (!normalized.ContainsKey("ConnectionStrings:DefaultNodeDb"))
        {
            normalized["ConnectionStrings:DefaultNodeDb"] = Path.Combine(dataDirectory, "node.db");
        }

        NormalizePath(normalized, "OracleKeyPath", dataDirectory);
        NormalizePath(normalized, "Consensus:ProducerPrivateKeyPath", dataDirectory);
        NormalizePath(normalized, "P2P:IdentityKeyPath", dataDirectory);
        NormalizePath(normalized, "Raft:LogPath", dataDirectory);
        NormalizePath(normalized, "Raft:MembershipPath", dataDirectory);
        NormalizePath(normalized, "Raft:SnapshotPath", dataDirectory);

        return normalized;
    }

    private static void NormalizePath(IDictionary<string, string> values, string key, string dataDirectory)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (Path.IsPathFullyQualified(value))
        {
            return;
        }

        values[key] = Path.GetFullPath(Path.Combine(dataDirectory, value));
    }

    private static NetworkInviteDocument SignInvite(
        IReadOnlyDictionary<string, string> configuration,
        NetworkInviteDocument invite)
    {
        if (!configuration.TryGetValue("P2P:IdentityKeyPath", out var identityPath) ||
            string.IsNullOrWhiteSpace(identityPath))
        {
            return invite;
        }

        using var key = LoadOrCreateSetupIdentity(identityPath);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string fingerprint = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey))[..8]).ToLowerInvariant();
        var unsigned = invite with
        {
            BootstrapPublicKey = publicKey,
            BootstrapFingerprint = fingerprint,
            Signature = string.Empty
        };
        byte[] signature = key.SignData(
            Encoding.UTF8.GetBytes(NexusSetupUseCases.BuildInviteCanonicalPayload(unsigned)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return unsigned with { Signature = Convert.ToBase64String(signature) };
    }

    private static ECDsa LoadOrCreateSetupIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var key = ECDsa.Create();
        if (File.Exists(fullPath))
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(fullPath).Trim()), out _);
            return key;
        }

        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fullPath, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        return key;
    }

    private static async Task WriteSetupStateAsync(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        string configPath = GetSetupConfigPath(configuration);
        string? directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string stagingPath = configPath + ".staging";
        await File.WriteAllTextAsync(
            stagingPath,
            JsonSerializer.Serialize(ToNestedJson(values), new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        if (File.Exists(configPath))
        {
            File.Replace(stagingPath, configPath, configPath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(stagingPath, configPath);
        }
    }

    private static string GetSetupConfigPath(IConfiguration configuration)
    {
        string? configured = configuration["NexusSetupConfigPath"] ?? Environment.GetEnvironmentVariable("NEXUS_SETUP_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string dataDirectory = GetSetupDataDirectory(configuration);
        return Path.Combine(dataDirectory, "nexus.setup.json");
    }

    private static string GetSetupDataDirectory(IConfiguration configuration)
    {
        string? configured = configuration["NexusSetupDataPath"] ?? Environment.GetEnvironmentVariable("NEXUS_SETUP_DATA_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string data = Environment.GetEnvironmentVariable("NEXUS_DATA_PATH") ?? string.Empty;
        return string.IsNullOrWhiteSpace(data) ? AppContext.BaseDirectory : data;
    }

    private static Dictionary<string, string?> ToDictionary(IConfiguration configuration) =>
        configuration.AsEnumerable().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

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
}

public sealed record InviteValidationRequest(string Invite);
public sealed record SetupBackupRequest(string Password);
