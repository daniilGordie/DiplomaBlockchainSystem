using System.Security.Cryptography;
using System.Text;
using Blockchain.Core;
using Blockchain.Node.Services;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Endpoints;

public static class IrohP2PEndpoints
{
    public static IEndpointRouteBuilder MapIrohP2PEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/p2p/iroh/known-channels", (
            HttpContext context,
            IChainReader chainReader,
            IOptions<P2POptions> options) =>
        {
            if (!IsAuthorized(context, options.Value))
            {
                return Results.Unauthorized();
            }

            return Results.Json(new IrohKnownChannelsResponse(chainReader.GetKnownChannels().ToArray()));
        });

        endpoints.MapGet("/api/p2p/iroh/chain/{channelId}", (
            string channelId,
            HttpContext context,
            IChainReader chainReader,
            IOptions<P2POptions> options) =>
        {
            if (!IsAuthorized(context, options.Value))
            {
                return Results.Unauthorized();
            }

            string channelToRead = string.IsNullOrWhiteSpace(channelId) ? "System" : channelId;
            var blocks = chainReader.LoadChain(channelToRead)
                .OrderBy(block => block.Index)
                .Select(GrpcProjectMapper.ToBlockModel)
                .ToArray();

            return Results.Json(new IrohChainResponse(blocks));
        });

        return endpoints;
    }

    private static bool IsAuthorized(HttpContext context, P2POptions options)
    {
        if (!options.Iroh.Enabled)
        {
            return false;
        }

        string configuredToken = options.Iroh.LocalApiToken;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return false;
        }

        string suppliedToken = context.Request.Headers["X-Nexus-Iroh-Token"].ToString();
        if (string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        byte[] left = Encoding.UTF8.GetBytes(configuredToken);
        byte[] right = Encoding.UTF8.GetBytes(suppliedToken);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
