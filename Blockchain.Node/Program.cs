using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node;
using Blockchain.Node.Endpoints;
using Blockchain.Node.Services;
using System.Threading.RateLimiting;
using Blockchain.Node.Hubs;

var builder = WebApplication.CreateBuilder(args);

string oraclePublicKeyConfig = GetRequiredConfiguration(builder.Configuration, "OraclePublicKey");
bool autoOraclePublicKey = string.Equals(oraclePublicKeyConfig, "auto", StringComparison.OrdinalIgnoreCase);
if (!autoOraclePublicKey)
{
    Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey = oraclePublicKeyConfig;
}

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

var databaseManager = new DatabaseManager(dbName, dbPassword);
builder.Services.AddNexusNodeServices(databaseManager);

var app = builder.Build();

var oracleKeyPair = app.Services.GetRequiredService<OracleIdentity>();
if (autoOraclePublicKey)
{
    Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey = oracleKeyPair.PublicKey;
}
else if (!string.Equals(oracleKeyPair.PublicKey, Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey, StringComparison.Ordinal))
{
    throw new InvalidOperationException("OraclePublicKey configuration does not match OracleIdentity public key. Update secrets before starting the node.");
}

app.UseRouting();
app.UseCors("AllowAll");
app.UseRateLimiter();
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

app.MapGrpcService<BlockchainGrpcService>().EnableGrpcWeb().RequireCors("AllowAll");
app.MapHub<BlockchainHub>("/blockchainHub").RequireCors("AllowAll");

app.MapGitIntegrationEndpoints(webhookSecret);
app.MapArtifactIntegrationEndpoints();
app.MapIpfsIntegrationEndpoints();

app.MapGet("/", () => "Nexus P2P Node is running. Use gRPC-Web to connect.");

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
