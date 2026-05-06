using Blockchain.Core;
using Blockchain.Node.Services;
using System.Threading.RateLimiting;
using Blockchain.Node.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey =
    builder.Configuration["OraclePublicKey"]
    ?? throw new InvalidOperationException("Ключ OraclePublicKey не найден в appsettings.json");

string webhookSecret = builder.Configuration["WebhookSecret"]
    ?? throw new InvalidOperationException("Секрет WebhookSecret не найден в appsettings.json");

builder.Services.AddCors(o => o.AddPolicy("AllowAll", builder =>
{
    builder.WithOrigins("https://localhost:7071", "http://localhost:7071")
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
        await context.HttpContext.Response.WriteAsync("⛔ Too many requests. DDoS protection enabled. Please wait.", token);
    };
});

var port = builder.Configuration["Urls"]?.Split(':').LastOrDefault()?.Replace("/", "") ?? "5041";
string dbName = $"nexus_node_{port}.db";
Console.WriteLine($"[SYSTEM] Starting Node. Database: {dbName}");

builder.Services.AddSingleton(new BlockchainManager(dbName));
builder.Services.AddSingleton<P2PNetworkService>();
builder.Services.AddSingleton<OracleIdentity>();

var app = builder.Build();

app.UseRouting();
app.UseCors("AllowAll");
app.UseRateLimiter();
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

app.MapGrpcService<BlockchainGrpcService>().EnableGrpcWeb().RequireCors("AllowAll");
app.MapHub<BlockchainHub>("/blockchainHub").RequireCors("AllowAll");

app.MapPost("/api/webhooks/git", async (HttpRequest request, IHubContext<BlockchainHub> hubContext) =>
{
    if (!request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader))
    {
        Console.WriteLine("[Security] Отказ: Отсутствует заголовок X-Hub-Signature-256.");
        return Results.Unauthorized();
    }

    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    string payload = await reader.ReadToEndAsync();

    string expectedSignature = "sha256=" + ComputeHmacSha256(payload, webhookSecret);
    if (!string.Equals(signatureHeader.ToString(), expectedSignature, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("[Security] Отказ: Неверная криптографическая подпись вебхука.");
        return Results.Unauthorized();
    }

    var intent = JsonSerializer.Deserialize<GitCommitIntent>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (intent == null || string.IsNullOrEmpty(intent.Author) || string.IsNullOrEmpty(intent.CommitHash))
    {
        return Results.BadRequest("Invalid payload");
    }

    string userGroup = $"USER_{intent.Author}";
    await hubContext.Clients.Group(userGroup).SendAsync("PendingCommitReceived", intent);

    return Results.Ok();
});

app.MapGet("/", () => "Nexus P2P Node is running. Use gRPC-Web to connect.");

app.Run();


static string ComputeHmacSha256(string payload, string secret)
{
    byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
    byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

    using var hmac = new HMACSHA256(secretBytes);
    byte[] hashBytes = hmac.ComputeHash(payloadBytes);

    return Convert.ToHexString(hashBytes).ToLowerInvariant();
}

public record GitCommitIntent(string Repository, string CommitHash, string Message, string Author, string PatchCid);