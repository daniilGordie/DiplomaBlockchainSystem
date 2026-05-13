using Blockchain.Core;
using Blockchain.Node;
using Blockchain.Node.Services;
using System.Threading.RateLimiting;
using Blockchain.Node.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using System.IO;
using System;
using System.Linq;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey =
    GetRequiredConfiguration(builder.Configuration, "OraclePublicKey");

string webhookSecret = GetRequiredConfiguration(builder.Configuration, "WebhookSecret");

builder.Services.AddCors(o => o.AddPolicy("AllowAll", policy =>
{
    policy.SetIsOriginAllowed(origin =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "127.0.0.1"
            || uri.Host == "::1"))
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()
        .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding");
}));

builder.Services.AddGrpc();
builder.Services.AddSignalR();

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 50,
                Window = TimeSpan.FromSeconds(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            });
    });

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsync("Too many requests. DDoS protection enabled. Please wait.", token);
    };
});

string dbName = GetNodeDatabaseName(builder.Configuration);
Console.WriteLine($"[SYSTEM] Starting Node. Database: {dbName}");

string dbPassword = GetRequiredConfiguration(builder.Configuration, "NodeDbPassword");

builder.Services.AddSingleton(new BlockchainManager(dbName, dbPassword));

builder.Services.AddSingleton<P2PNetworkService>();
builder.Services.AddHostedService<P2PBootstrapService>();
builder.Services.AddSingleton<OracleIdentity>();
builder.Services.AddSingleton<WebhookReplayGuard>();
builder.Services.AddSingleton<GitProjectBindingStore>();

var app = builder.Build();

var oracleKeyPair = app.Services.GetRequiredService<OracleIdentity>();
if (!string.Equals(oracleKeyPair.PublicKey, Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey, StringComparison.Ordinal))
{
    throw new InvalidOperationException("OraclePublicKey configuration does not match OracleIdentity public key. Update secrets before starting the node.");
}

app.UseRouting();
app.UseCors("AllowAll");
app.UseRateLimiter();
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

app.MapGrpcService<BlockchainGrpcService>().EnableGrpcWeb().RequireCors("AllowAll");
app.MapHub<BlockchainHub>("/blockchainHub").RequireCors("AllowAll");

app.MapPost("/api/webhooks/git", async (
    HttpRequest request,
    IConfiguration configuration,
    IHubContext<BlockchainHub> hubContext,
    BlockchainManager blockchainManager,
    OracleIdentity oracleIdentity,
    P2PNetworkService p2pService,
    GitProjectBindingStore bindingStore,
    WebhookReplayGuard replayGuard) =>
{
    if (!request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader))
    {
        Console.WriteLine("[Security] Reject: Header is missing X-Hub-Signature-256.");
        return Results.Unauthorized();
    }

    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    string payload = await reader.ReadToEndAsync();

    if (!GitWebhookSecurity.IsValidSignature(payload, webhookSecret, signatureHeader.ToString()))
    {
        Console.WriteLine("[Security] Reject: Wrong crypto webhook sign.");
        return Results.Unauthorized();
    }

    string defaultProjectId =
        configuration["GitWebhookDefaultProjectId"]
        ?? Environment.GetEnvironmentVariable("NEXUS_PROJECT_ID")
        ?? string.Empty;

    var intent = ParseGitIntent(payload, request, defaultProjectId);
    if (intent == null || string.IsNullOrWhiteSpace(intent.Author) || string.IsNullOrWhiteSpace(intent.CommitHash))
    {
        return Results.BadRequest("Invalid git payload. Expected Nexus hook payload or supported Git provider push payload.");
    }

    if (!GitWebhookSecurity.IsValidCommitHash(intent.CommitHash))
    {
        return Results.BadRequest("Invalid commit hash format");
    }

    if (!GitWebhookSecurity.IsValidProjectId(intent.ProjectId))
    {
        return Results.BadRequest("Invalid project id");
    }

    if (!GitWebhookSecurity.IsValidRepository(intent.Repository))
    {
        return Results.BadRequest("Invalid repository field");
    }

    if (!bindingStore.TryValidateOrBind(intent.Repository, intent.ProjectId, out var bindingMessage))
    {
        return Results.BadRequest(bindingMessage);
    }

    var replayKey = $"{intent.ProjectId}:{intent.Repository}:{intent.CommitHash}".ToLowerInvariant();
    if (!replayGuard.TryRegister(replayKey, DateTime.UtcNow))
    {
        return Results.Ok(new { status = "duplicate_ignored" });
    }

    string channelId = intent.ProjectId;
    var latest = blockchainManager.GetLatestBlock(channelId);
    int nextIndex = latest != null ? latest.Index + 1 : 0;
    string prevHash = latest != null ? latest.Hash : "0";
    string timestamp = DateTime.UtcNow.ToString("O");

    var payloadObject = new
    {
        Type = "CodeCommit",
        Source = "GitEvent",
        Provider = intent.Provider,
        Repository = intent.Repository,
        Branch = intent.Branch,
        CommitHash = intent.CommitHash,
        Message = intent.Message ?? string.Empty,
        User = intent.Author,
        PatchCid = intent.PatchCid ?? string.Empty,
        ProjectId = intent.ProjectId,
        Timestamp = timestamp
    };

    string blockData = JsonSerializer.Serialize(payloadObject);
    string signableData = $"{nextIndex}{timestamp}{blockData}{prevHash}";
    string oracleSignature = oracleIdentity.SignData(signableData);

    var block = new Block
    {
        Index = nextIndex,
        Timestamp = DateTime.Parse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
        Data = blockData,
        PreviousHash = prevHash,
        ValidatorPublicKey = oracleIdentity.PublicKey,
        Signature = oracleSignature,
        ChannelId = channelId
    };

    blockchainManager.MineBlock(block);
    bool accepted = blockchainManager.ProcessPeerBlock(block);

    if (!accepted)
    {
        return Results.BadRequest(new { status = "rejected", reason = "blockchain_validation_failed" });
    }

    await hubContext.Clients.Group(channelId).SendAsync("NewBlockBroadcast", new BlockModel
    {
        Index = block.Index,
        Timestamp = block.Timestamp.ToString("O"),
        Data = block.Data,
        PreviousHash = block.PreviousHash,
        Hash = block.Hash,
        ValidatorPublicKey = block.ValidatorPublicKey ?? string.Empty,
        Signature = block.Signature ?? string.Empty,
        Nonce = block.Nonce,
        ChannelId = block.ChannelId
    });

    await p2pService.BroadcastBlockAsync(block);
    return Results.Ok(new { status = "anchored", blockHash = block.Hash, channelId });
});

app.MapPost("/api/integrations/artifacts/register", async (
    ArtifactAnchorIntent intent,
    IHubContext<BlockchainHub> hubContext,
    BlockchainManager blockchainManager,
    OracleIdentity oracleIdentity,
    P2PNetworkService p2pService,
    WebhookReplayGuard replayGuard) =>
{
    if (intent == null)
    {
        return Results.BadRequest("Invalid payload");
    }

    string actor = string.IsNullOrWhiteSpace(intent.RegisteredBy) ? intent.User : intent.RegisteredBy;
    if (string.IsNullOrWhiteSpace(actor) ||
        string.IsNullOrWhiteSpace(intent.ProjectId) ||
        string.IsNullOrWhiteSpace(intent.FileHash) ||
        string.IsNullOrWhiteSpace(intent.FileName) ||
        string.IsNullOrWhiteSpace(intent.UserPublicKey) ||
        string.IsNullOrWhiteSpace(intent.UserSignature))
    {
        return Results.BadRequest("Missing required fields");
    }

    if (!GitWebhookSecurity.IsValidProjectId(intent.ProjectId))
    {
        return Results.BadRequest("Invalid project id");
    }

    if (!IsLikelyIpfsCid(intent.FileHash))
    {
        return Results.BadRequest("Invalid IPFS CID format");
    }

    if (intent.FileName.Length > 256)
    {
        return Results.BadRequest("Invalid file name");
    }

    if (intent.SizeBytes <= 0 || intent.SizeBytes > 50L * 1024 * 1024)
    {
        return Results.BadRequest("Invalid file size");
    }

    if (!DateTime.TryParse(intent.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var clientTimestamp))
    {
        return Results.BadRequest("Invalid timestamp");
    }

    if ((DateTime.UtcNow - clientTimestamp.ToUniversalTime()).Duration() > TimeSpan.FromMinutes(10))
    {
        return Results.BadRequest("Expired request signature");
    }

    string signable = $"ARTIFACT_REGISTER:{intent.ProjectId}:{intent.FileHash}:{actor}:{intent.Timestamp}";
    if (!VerifySignature(signable, intent.UserSignature, intent.UserPublicKey))
    {
        return Results.Unauthorized();
    }

    var db = new DatabaseManager(dbName, dbPassword);
    string? boundPublicKey = db.GetUserPublicKey(actor);
    if (string.IsNullOrWhiteSpace(boundPublicKey) ||
        !string.Equals(boundPublicKey, intent.UserPublicKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    string role = db.GetUserRole(intent.ProjectId, actor);
    if (role == "None")
    {
        return Results.Forbid();
    }

    var replayKey = $"{intent.ProjectId}:{intent.FileHash}:{actor}".ToLowerInvariant();
    if (!replayGuard.TryRegister(replayKey, DateTime.UtcNow))
    {
        return Results.Ok(new { status = "duplicate_ignored" });
    }

    string channelId = intent.ProjectId;
    var latest = blockchainManager.GetLatestBlock(channelId);
    int nextIndex = latest != null ? latest.Index + 1 : 0;
    string prevHash = latest != null ? latest.Hash : "0";
    string timestamp = DateTime.UtcNow.ToString("O");

    var payloadObject = new
    {
        Source = "ArtifactRegistry",
        Type = "Register",
        FileName = intent.FileName,
        FileHash = intent.FileHash,
        SizeBytes = intent.SizeBytes,
        ContentType = string.IsNullOrWhiteSpace(intent.ContentType) ? "application/octet-stream" : intent.ContentType,
        User = actor,
        ProjectId = intent.ProjectId,
        RegisteredBy = actor,
        VerificationMethod = string.IsNullOrWhiteSpace(intent.VerificationMethod) ? "IPFS CID" : intent.VerificationMethod,
        Timestamp = timestamp
    };

    string blockData = JsonSerializer.Serialize(payloadObject);
    string signableData = $"{nextIndex}{timestamp}{blockData}{prevHash}";
    string oracleSignature = oracleIdentity.SignData(signableData);

    var block = new Block
    {
        Index = nextIndex,
        Timestamp = DateTime.Parse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
        Data = blockData,
        PreviousHash = prevHash,
        ValidatorPublicKey = oracleIdentity.PublicKey,
        Signature = oracleSignature,
        ChannelId = channelId
    };

    blockchainManager.MineBlock(block);
    bool accepted = blockchainManager.ProcessPeerBlock(block);

    if (!accepted)
    {
        return Results.BadRequest(new { status = "rejected", reason = "blockchain_validation_failed" });
    }

    await hubContext.Clients.Group(channelId).SendAsync("NewBlockBroadcast", new BlockModel
    {
        Index = block.Index,
        Timestamp = block.Timestamp.ToString("O"),
        Data = block.Data,
        PreviousHash = block.PreviousHash,
        Hash = block.Hash,
        ValidatorPublicKey = block.ValidatorPublicKey ?? string.Empty,
        Signature = block.Signature ?? string.Empty,
        Nonce = block.Nonce,
        ChannelId = block.ChannelId
    });

    await p2pService.BroadcastBlockAsync(block);
    return Results.Ok(new { status = "anchored", blockHash = block.Hash, channelId, cid = intent.FileHash });
});

app.MapGet("/", () => "Nexus P2P Node is running. Use gRPC-Web to connect.");

app.MapGet("/api/integrations/git/status", (IConfiguration configuration, GitProjectBindingStore bindingStore) =>
{
    string publicUrl = configuration["P2P:PublicUrl"] ?? string.Empty;
    string defaultProjectId =
        configuration["GitWebhookDefaultProjectId"]
        ?? Environment.GetEnvironmentVariable("NEXUS_PROJECT_ID")
        ?? string.Empty;
    var bindings = bindingStore.GetBindingsSnapshot();
    string webhookUrl = string.IsNullOrWhiteSpace(publicUrl)
        ? "/api/webhooks/git"
        : $"{publicUrl.TrimEnd('/')}/api/webhooks/git";

    return Results.Ok(new
    {
        integration = "git",
        mode = "trusted-oracle-auto-anchor",
        webhookUrl,
        source = "GitEvent",
        providers = new[] { "NexusGitHook", "GitHubPush" },
        acceptsPayloads = new[] { "NexusGitHook", "GitHubPush" },
        defaultProjectId = string.IsNullOrWhiteSpace(defaultProjectId) ? "(not set)" : defaultProjectId,
        bindingPolicy = "one-repository-per-project",
        bindingsCount = bindings.Count,
        bindings = bindings.Select(x => new { repository = x.Key, projectId = x.Value }).ToArray(),
        webhookSecretConfigured = !string.IsNullOrWhiteSpace(configuration["WebhookSecret"]),
        projectIdSource = "payload.projectId or ?projectId=... or GitWebhookDefaultProjectId/NEXUS_PROJECT_ID",
        queueFile = "pending_hooks.json"
    });
});

app.MapGet("/api/integrations/ipfs/health", async (IConfiguration configuration) =>
{
    string apiBase = (configuration["IpfsApiUrl"] ?? "http://127.0.0.1:5001/api/v0").Trim().TrimEnd('/');
    if (apiBase.EndsWith("/add", StringComparison.OrdinalIgnoreCase))
    {
        apiBase = apiBase[..^4];
    }

    string idUrl = $"{apiBase}/id";
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.PostAsync(idUrl, content: null);
        return Results.Ok(new
        {
            integration = "ipfs",
            apiUrl = apiBase,
            reachable = response.IsSuccessStatusCode,
            statusCode = (int)response.StatusCode
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            integration = "ipfs",
            apiUrl = apiBase,
            reachable = false,
            error = ex.Message
        });
    }
});

app.Run();

static bool VerifySignature(string data, string signatureBase64, string publicKeyBase64)
{
    try
    {
        byte[] signatureBytes = Convert.FromBase64String(signatureBase64);
        if (signatureBytes.Length != 64)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        return ecdsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256);
    }
    catch
    {
        return false;
    }
}

static bool IsLikelyIpfsCid(string cid)
{
    if (string.IsNullOrWhiteSpace(cid) || cid.Length < 40 || cid.Length > 128)
    {
        return false;
    }

    foreach (char c in cid)
    {
        bool ok = (c >= 'a' && c <= 'z') ||
                  (c >= 'A' && c <= 'Z') ||
                  (c >= '0' && c <= '9');
        if (!ok)
        {
            return false;
        }
    }

    return true;
}


static string GetRequiredConfiguration(IConfiguration configuration, string key)
{
    string? value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            $"{key} is not configured. Set it via .NET user-secrets or environment variables.");
    }

    return value;
}

static string GetNodeDatabaseName(IConfiguration configuration)
{
    string port = configuration["Urls"]?.Split(':').LastOrDefault()?.Replace("/", "") ?? "5041";
    string? configuredDbName = configuration.GetConnectionString("DefaultNodeDb");

    if (string.IsNullOrWhiteSpace(configuredDbName) ||
        (configuredDbName == "nexus_node_5041.db" && port != "5041"))
    {
        return $"nexus_node_{port}.db";
    }

    return configuredDbName;
}

static GitCommitIntent? ParseGitIntent(string payload, HttpRequest request, string defaultProjectId)
{
    try
    {
        var hookIntent = JsonSerializer.Deserialize<GitCommitIntent>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (hookIntent != null &&
            !string.IsNullOrWhiteSpace(hookIntent.Repository) &&
            !string.IsNullOrWhiteSpace(hookIntent.CommitHash))
        {
            return hookIntent with
            {
                Provider = string.IsNullOrWhiteSpace(hookIntent.Provider)
                    ? "NexusGitHook"
                    : hookIntent.Provider
            };
        }
    }
    catch
    {
        // Ignore and fallback to GitHub push shape.
    }

    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        string eventType = request.Headers.TryGetValue("X-GitHub-Event", out var eventHeader)
            ? eventHeader.ToString()
            : string.Empty;

        if (!string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string repository = "";
        if (root.TryGetProperty("repository", out var repoNode))
        {
            repository =
                repoNode.TryGetProperty("full_name", out var fullName) ? fullName.GetString() ?? "" :
                repoNode.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
        }

        if (!root.TryGetProperty("head_commit", out var headCommit) || headCommit.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string commitHash = headCommit.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        string message = headCommit.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        string author = "";
        if (headCommit.TryGetProperty("author", out var authorNode))
        {
            author =
                authorNode.TryGetProperty("username", out var username) ? username.GetString() ?? "" :
                authorNode.TryGetProperty("name", out var authorName) ? authorName.GetString() ?? "" : "";
        }

        if (string.IsNullOrWhiteSpace(author) && root.TryGetProperty("sender", out var sender))
        {
            author = sender.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
        }

        string gitRef = root.TryGetProperty("ref", out var refProp) ? refProp.GetString() ?? "" : "";
        string branch = gitRef.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? gitRef["refs/heads/".Length..]
            : gitRef;

        string queryProjectId = request.Query.TryGetValue("projectId", out var projectValues) ? projectValues.ToString() : "";
        string projectId = !string.IsNullOrWhiteSpace(queryProjectId) ? queryProjectId : defaultProjectId;

        return new GitCommitIntent(
            Repository: repository,
            CommitHash: commitHash,
            Message: message,
            Author: author,
            PatchCid: "",
            ProjectId: projectId,
            Provider: "GitHubPush",
            Branch: branch);
    }
    catch
    {
        return null;
    }
}

public record GitCommitIntent(
    string Repository,
    string CommitHash,
    string Message,
    string Author,
    string PatchCid,
    string ProjectId,
    string Provider = "NexusGitHook",
    string Branch = "");

public record ArtifactAnchorIntent(
    string ProjectId,
    string FileHash,
    string FileName,
    long SizeBytes,
    string ContentType,
    string User,
    string RegisteredBy,
    string VerificationMethod,
    string Timestamp,
    string UserPublicKey,
    string UserSignature);
