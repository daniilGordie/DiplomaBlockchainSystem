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
    IHubContext<BlockchainHub> hubContext,
    BlockchainManager blockchainManager,
    OracleIdentity oracleIdentity,
    P2PNetworkService p2pService,
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

    var intent = JsonSerializer.Deserialize<GitCommitIntent>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (intent == null || string.IsNullOrEmpty(intent.Author) || string.IsNullOrEmpty(intent.CommitHash))
    {
        return Results.BadRequest("Invalid payload");
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

    var replayKey = $"{intent.ProjectId}:{intent.CommitHash}:{intent.Author}".ToLowerInvariant();
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
        Source = "GitHub",
        Repository = intent.Repository,
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

app.MapGet("/", () => "Nexus P2P Node is running. Use gRPC-Web to connect.");

app.MapGet("/api/integrations/git/status", (IConfiguration configuration) =>
{
    string publicUrl = configuration["P2P:PublicUrl"] ?? string.Empty;
    string webhookUrl = string.IsNullOrWhiteSpace(publicUrl)
        ? "/api/webhooks/git"
        : $"{publicUrl.TrimEnd('/')}/api/webhooks/git";

    return Results.Ok(new
    {
        integration = "git",
        mode = "trusted-oracle-auto-anchor",
        webhookUrl,
        webhookSecretConfigured = !string.IsNullOrWhiteSpace(configuration["WebhookSecret"]),
        projectIdSource = "NEXUS_PROJECT_ID or hook appsettings.json",
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

public record GitCommitIntent(string Repository, string CommitHash, string Message, string Author, string PatchCid, string ProjectId);
