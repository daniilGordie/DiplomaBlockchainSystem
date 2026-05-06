using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Blockchain.UI.Services;
using Blockchain.Node;
using Blockchain.UI.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<KeyService>();
builder.Services.AddScoped<MnemonicService>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddScoped<IpfsService>();

var nodeUrl = builder.Configuration["NodeUrl"] ?? "http://localhost:5041";

builder.Services.AddScoped(services =>
{
    var httpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());

    var channel = GrpcChannel.ForAddress(nodeUrl, new GrpcChannelOptions
    {
        HttpHandler = httpHandler
    });

    return new BlockchainService.BlockchainServiceClient(channel);
});

await builder.Build().RunAsync();