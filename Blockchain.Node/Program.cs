using Blockchain.Core;
using Blockchain.Node.Services;
using System.Threading.RateLimiting; 

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddPolicy("AllowAll", builder =>
{
    builder.AllowAnyOrigin()
           .AllowAnyMethod()
           .AllowAnyHeader()
           .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding");
}));

builder.Services.AddGrpc();

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

app.UseRateLimiter(); 
app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
app.UseCors("AllowAll"); 

app.MapGrpcService<BlockchainGrpcService>().EnableGrpcWeb();
app.MapGet("/", () => "Nexus P2P Node is running. Use gRPC-Web to connect.");

app.Run();