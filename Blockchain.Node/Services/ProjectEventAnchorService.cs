using System.Text.Json;
using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Contracts;
using Blockchain.Core.Consensus;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class ProjectEventAnchorService
{
    private readonly BlockchainManager _blockchainManager;
    private readonly OracleIdentity _oracleIdentity;
    private readonly ProducerIdentity _producerIdentity;
    private readonly ConsensusOptions _consensusOptions;
    private readonly BlockProposalFactory _blockProposalFactory;
    private readonly VerifyBlockProposalUseCase _verifyBlockProposal;
    private readonly IBlockFinalitySubmitter _blockFinalitySubmitter;
    private readonly CommittedBlockApplier _committedBlockApplier;

    public ProjectEventAnchorService(
        BlockchainManager blockchainManager,
        OracleIdentity oracleIdentity,
        ProducerIdentity producerIdentity,
        IOptions<ConsensusOptions> consensusOptions,
        BlockProposalFactory blockProposalFactory,
        VerifyBlockProposalUseCase verifyBlockProposal,
        IBlockFinalitySubmitter blockFinalitySubmitter,
        CommittedBlockApplier committedBlockApplier)
    {
        _blockchainManager = blockchainManager;
        _oracleIdentity = oracleIdentity;
        _producerIdentity = producerIdentity;
        _consensusOptions = consensusOptions.Value;
        _blockProposalFactory = blockProposalFactory;
        _verifyBlockProposal = verifyBlockProposal;
        _blockFinalitySubmitter = blockFinalitySubmitter;
        _committedBlockApplier = committedBlockApplier;
    }

    public async Task<ProjectEventAnchorResult> AnchorAsync<TPayload>(
        string channelId,
        TPayload payload,
        string timestamp)
    {
        var latest = _blockchainManager.GetLatestBlock(channelId);
        int nextIndex = latest != null ? latest.Index + 1 : 0;
        string previousHash = latest != null ? latest.Hash : "0";
        string sourcePayloadData = JsonSerializer.Serialize(payload);
        string blockData = AddOracleAttestation(sourcePayloadData);
        var blockTimestamp = DateTimeOffset.Parse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        long timestampUnixSeconds = blockTimestamp.ToUnixTimeSeconds();
        string signableData = $"{nextIndex}{timestampUnixSeconds}{blockData}{previousHash}";
        string blockSignerPublicKey = _oracleIdentity.PublicKey;
        string blockSignature = _oracleIdentity.SignData(signableData);

        var block = new Block
        {
            Index = nextIndex,
            Timestamp = blockTimestamp.UtcDateTime,
            TimestampUnixSeconds = timestampUnixSeconds,
            Data = blockData,
            PreviousHash = previousHash,
            ValidatorPublicKey = blockSignerPublicKey,
            Signature = blockSignature,
            ChannelId = channelId
        };

        BlockWriteResult result;
        if (_consensusOptions.EnableProofOfContributionValidation)
        {
            var proposal = _blockProposalFactory.BuildImplicitProposal(block);
            if (!proposal.Accepted || proposal.Proposal == null)
            {
                return new ProjectEventAnchorResult(false, channelId, "", $"poc_proposal_rejected: {proposal.Reason}");
            }

            if (!_producerIdentity.IsConfigured)
            {
                return new ProjectEventAnchorResult(
                    false,
                    channelId,
                    "",
                    "poc_producer_key_unavailable: Consensus:ProducerKeyPath must be configured on the selected producer node for oracle/Git/IPFS events");
            }

            if (!string.Equals(_producerIdentity.PublicKey, proposal.Proposal.ContributionProof.ProducerPublicKey, StringComparison.Ordinal))
            {
                return new ProjectEventAnchorResult(
                    false,
                    channelId,
                    "",
                    "poc_producer_key_mismatch: local producer key does not match the deterministic PoC producer for this proposal");
            }

            block.ValidatorPublicKey = _producerIdentity.PublicKey;
            block.Signature = _producerIdentity.SignData(signableData);
            _blockchainManager.MineBlock(block);
            var producerSignedProposal = new BlockProposal(
                block,
                proposal.Proposal.ContributionProof,
                DateTime.UtcNow);
            var blockModel = GrpcProjectMapper.ToBlockModel(block);
            blockModel.ContributionProof = GrpcProjectMapper.ToContributionProofModel(producerSignedProposal.ContributionProof);

            var verification = _verifyBlockProposal.Execute(producerSignedProposal);
            if (!verification.Accepted)
            {
                return new ProjectEventAnchorResult(false, channelId, "", $"poc_verification_rejected: {verification.Reason}");
            }

            result = await _blockFinalitySubmitter.SubmitAsync(producerSignedProposal, blockModel);
        }
        else
        {
            _blockchainManager.MineBlock(block);
            var blockModel = GrpcProjectMapper.ToBlockModel(block);
            result = await _committedBlockApplier.ApplyAsync(block, blockModel, broadcastToPeers: true);
        }

        if (!result.Success)
        {
            return new ProjectEventAnchorResult(false, channelId, "", "blockchain_validation_failed");
        }

        return new ProjectEventAnchorResult(true, channelId, block.Hash, "");
    }

    private string AddOracleAttestation(string sourcePayloadData)
    {
        string signedPayloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sourcePayloadData));
        string oracleSignature = _oracleIdentity.SignData(signedPayloadBase64);

        try
        {
            using var doc = JsonDocument.Parse(sourcePayloadData);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }

                    writer.WriteString(OracleAttestation.PublicKeyProperty, _oracleIdentity.PublicKey);
                    writer.WriteString(OracleAttestation.SignatureProperty, oracleSignature);
                    writer.WriteString(OracleAttestation.SignedPayloadProperty, signedPayloadBase64);
                    writer.WriteEndObject();
                }

                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        catch (JsonException)
        {
        }

        return JsonSerializer.Serialize(new
        {
            Payload = sourcePayloadData,
            OraclePublicKey = _oracleIdentity.PublicKey,
            OracleSignature = oracleSignature,
            OracleSignedPayloadBase64 = signedPayloadBase64
        });
    }
}

public sealed record ProjectEventAnchorResult(
    bool Accepted,
    string ChannelId,
    string BlockHash,
    string Error);
