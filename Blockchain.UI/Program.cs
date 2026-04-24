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

builder.Services.AddScoped(services =>
{
    var httpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());

    var channel = GrpcChannel.ForAddress("https://localhost:7066", new GrpcChannelOptions
    {
        HttpHandler = httpHandler
    });

    return new BlockchainService.BlockchainServiceClient(channel);
});

await builder.Build().RunAsync();