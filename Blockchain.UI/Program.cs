using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Blockchain.UI.Services;
using Blockchain.Node;
using Blockchain.UI.Components;
using Blockchain.UI.Application.Clients;
using Blockchain.UI.Application.Security;
using Blockchain.UI.Application.UseCases;
using Blockchain.UI.Infrastructure.Browser;
using Blockchain.UI.Infrastructure.Grpc;
using Blockchain.UI.Infrastructure.Http;
using Blockchain.UI.Infrastructure.SignalR;
using Microsoft.AspNetCore.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<KeyService>();
builder.Services.AddScoped<MnemonicService>();
builder.Services.AddScoped<IReadRequestAuthorizer, ReadRequestAuthorizer>();
builder.Services.AddScoped<INodeClientFactory, NodeClientFactory>();
builder.Services.AddScoped<IGitIntegrationClient, GitIntegrationClient>();
builder.Services.AddScoped<IIpfsIntegrationClient, IpfsIntegrationClient>();
builder.Services.AddScoped<IArtifactIntegrationClient, ArtifactIntegrationClient>();
builder.Services.AddScoped<IConsensusClient, ConsensusClient>();
builder.Services.AddScoped<IBlockchainRealtimeClient, BlockchainRealtimeClient>();
builder.Services.AddScoped<IProjectRepositoryDataService, ProjectRepositoryDataService>();
builder.Services.AddScoped<IProjectWorkspaceDataService, ProjectWorkspaceDataService>();
builder.Services.AddScoped<IBlockAnchoringService, BlockAnchoringService>();
builder.Services.AddScoped<IPeerNetworkClient, PeerNetworkClient>();
builder.Services.AddScoped<IIpfsGatewayClient, IpfsGatewayClient>();
builder.Services.AddScoped<DashboardActions>();
builder.Services.AddScoped<IClipboardService, ClipboardService>();
builder.Services.AddScoped<IBrowserOperationQueue, BrowserOperationQueue>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddScoped<IpfsService>();

builder.Services.AddScoped(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var navigation = services.GetRequiredService<NavigationManager>();
    string nodeUrl = NodeUrlResolver.Resolve(configuration, navigation);
    return services.GetRequiredService<INodeClientFactory>().Create(nodeUrl);
});

await builder.Build().RunAsync();
