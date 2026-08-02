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

        endpoints.MapGet("/api/p2p/iroh/committed-since/{channelId}", (
            string channelId,
            int? afterIndex,
            string? afterHash,
            HttpContext context,
            IChainReader chainReader,
            IBlockFinalityMetadataStore finalityMetadata,
            IOptions<P2POptions> options) =>
        {
            if (!IsAuthorized(context, options.Value))
            {
                return Results.Unauthorized();
            }

            string channelToRead = string.IsNullOrWhiteSpace(channelId) ? "System" : channelId;
            int minIndex = afterIndex ?? -1;
            string expectedAfterHash = afterHash ?? string.Empty;
            var chain = chainReader.LoadChain(channelToRead)
                .OrderBy(block => block.Index)
                .ToList();

            if (minIndex >= 0 && !string.IsNullOrWhiteSpace(expectedAfterHash))
            {
                var anchor = chain.FirstOrDefault(block => block.Index == minIndex);
                if (anchor == null || !string.Equals(anchor.Hash, expectedAfterHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new IrohCommittedSinceResponse(Array.Empty<IrohCommittedBlockEnvelope>()));
                }
            }

            var blocks = chain
                .Where(block => block.Index > minIndex)
                .Select(block => (Block: block, Metadata: finalityMetadata.GetFinalityMetadata(block.Hash)))
                .Where(item => item.Metadata != null)
                .Select(item => ToCommittedEnvelope(item.Block, item.Metadata!, finalityMetadata))
                .ToArray();

            return Results.Json(new IrohCommittedSinceResponse(blocks));
        });

        endpoints.MapPost("/api/p2p/iroh/committed-since", (
            IrohCommittedSincePayload request,
            HttpContext context,
            IChainReader chainReader,
            IBlockFinalityMetadataStore finalityMetadata,
            IrohMessageSecurity messageSecurity,
            IOptions<P2POptions> options) =>
        {
            if (!IsAuthorized(context, options.Value))
            {
                return Results.Unauthorized();
            }

            string payloadHash = IrohMessageSecurity.HashPayload($"{request.ChannelId}|{request.AfterIndex}|{request.AfterHash}");
            var validation = messageSecurity.Validate(request.Auth, IrohMessageOperations.CommittedSince, payloadHash);
            if (!validation.Accepted)
            {
                return Results.Unauthorized();
            }

            return BuildCommittedSinceResponse(
                request.ChannelId,
                request.AfterIndex,
                request.AfterHash,
                chainReader,
                finalityMetadata);
        });

        endpoints.MapPost("/api/network/committed-since", (
            IrohCommittedSincePayload request,
            IChainReader chainReader,
            IBlockFinalityMetadataStore finalityMetadata,
            IrohMessageSecurity messageSecurity) =>
        {
            string payloadHash = IrohMessageSecurity.HashPayload($"{request.ChannelId}|{request.AfterIndex}|{request.AfterHash}");
            var validation = messageSecurity.Validate(request.Auth, IrohMessageOperations.CommittedSince, payloadHash);
            if (!validation.Accepted)
            {
                return Results.Unauthorized();
            }

            return BuildCommittedSinceResponse(
                request.ChannelId,
                request.AfterIndex,
                request.AfterHash,
                chainReader,
                finalityMetadata);
        });

        endpoints.MapPost("/api/p2p/iroh/submit-block", async (
            IrohSubmitBlockPayload request,
            HttpContext context,
            GrpcBlockProcessor blockProcessor,
            IrohMessageSecurity messageSecurity,
            IOptions<P2POptions> options) =>
        {
            if (!IsAuthorized(context, options.Value))
            {
                return Results.Unauthorized();
            }

            string payloadHash = IrohMessageSecurity.HashPayload(request.Block.Hash);
            var validation = messageSecurity.Validate(request.Auth, IrohMessageOperations.SubmitBlock, payloadHash);
            if (!validation.Accepted)
            {
                return Results.Json(new IrohSubmitBlockResponse(
                    false,
                    validation.Message,
                    request.Block.ChannelId));
            }

            var result = await blockProcessor.ProcessReceivedAsync(request.Block, context.RequestAborted);
            return Results.Json(new IrohSubmitBlockResponse(
                result.Success,
                result.Message,
                result.ChannelId));
        });

        return endpoints;
    }

    private static IResult BuildCommittedSinceResponse(
        string channelId,
        int? afterIndex,
        string? afterHash,
        IChainReader chainReader,
        IBlockFinalityMetadataStore finalityMetadata)
    {
        string channelToRead = ChannelName.Normalize(string.IsNullOrWhiteSpace(channelId) ? "System" : channelId);
        int minIndex = afterIndex ?? -1;
        string expectedAfterHash = afterHash ?? string.Empty;
        var chain = chainReader.LoadChain(channelToRead)
            .OrderBy(block => block.Index)
            .ToList();

        if (minIndex >= 0 && !string.IsNullOrWhiteSpace(expectedAfterHash))
        {
            var anchor = chain.FirstOrDefault(block => block.Index == minIndex);
            if (anchor == null || !string.Equals(anchor.Hash, expectedAfterHash, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Conflict(new IrohCommittedSinceResponse(Array.Empty<IrohCommittedBlockEnvelope>()));
            }
        }

        var blocks = chain
            .Where(block => block.Index > minIndex)
            .Select(block => (Block: block, Metadata: finalityMetadata.GetFinalityMetadata(block.Hash)))
            .Where(item => item.Metadata != null)
            .Select(item => ToCommittedEnvelope(item.Block, item.Metadata!, finalityMetadata))
            .ToArray();

        return Results.Json(new IrohCommittedSinceResponse(blocks));
    }

    private static IrohCommittedBlockEnvelope ToCommittedEnvelope(
        Block block,
        BlockFinalityMetadata metadata,
        IBlockFinalityMetadataStore proofStore)
    {
        var model = GrpcProjectMapper.ToBlockModel(block);
        var proof = proofStore.GetContributionProof(block.Hash);
        if (proof != null)
        {
            model.ContributionProof = GrpcProjectMapper.ToContributionProofModel(proof);
        }

        return new IrohCommittedBlockEnvelope(
            model,
            ToIrohFinalityMetadata(metadata));
    }

    private static IrohFinalityMetadataModel ToIrohFinalityMetadata(BlockFinalityMetadata metadata) =>
        new(
            metadata.BlockHash,
            metadata.ChannelId,
            metadata.FinalityMode,
            metadata.RaftLogIndex,
            metadata.RaftTerm,
            metadata.CommittedAtUtc);

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
