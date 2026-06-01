using Blockchain.Core.Constants;
using Blockchain.Core.Contracts;

namespace Blockchain.Core;

public sealed class PeerBlockValidator
{
    private readonly IChainReader _chainReader;
    private readonly ISmartContractStateReader _smartContractState;
    private readonly ContractExecutor _executor;

    public PeerBlockValidator(
        IChainReader chainReader,
        ISmartContractStateReader smartContractState)
    {
        _chainReader = chainReader;
        _smartContractState = smartContractState;
        _executor = new ContractExecutor();
    }

    public PeerBlockValidationResult Validate(Block peerBlock)
    {
        peerBlock.ChannelId = string.IsNullOrWhiteSpace(peerBlock.ChannelId) ? "System" : peerBlock.ChannelId;

        if (!BlockPayloadChannelPolicy.IsConsistent(peerBlock.Data, peerBlock.ChannelId))
        {
            return PeerBlockValidationResult.Reject("payload/channel mismatch");
        }

        if (_chainReader.BlockExists(peerBlock.Hash, peerBlock.ChannelId))
        {
            return PeerBlockValidationResult.AlreadyAccepted();
        }

        var latestBlock = _chainReader.GetLatestBlock(peerBlock.ChannelId);
        int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
        string expectedPrevHash = latestBlock != null ? latestBlock.Hash : "0";

        if (peerBlock.Index != expectedIndex || peerBlock.PreviousHash != expectedPrevHash)
        {
            return PeerBlockValidationResult.Pending("Index or PreviousHash mismatch");
        }

        if (!peerBlock.VerifySignature()) return PeerBlockValidationResult.Reject("invalid signature");
        if (peerBlock.Hash != peerBlock.CalculateHash()) return PeerBlockValidationResult.Reject("invalid hash");
        if (!_executor.Execute(peerBlock.Data, peerBlock.ValidatorPublicKey, _smartContractState)) return PeerBlockValidationResult.Reject("contract rejected");
        if (!peerBlock.Hash.StartsWith(NetworkParameters.TargetPrefix)) return PeerBlockValidationResult.Reject("proof of work rejected");

        return PeerBlockValidationResult.Accept();
    }
}

public sealed record PeerBlockValidationResult(
    PeerBlockValidationStatus Status,
    string Reason)
{
    public static PeerBlockValidationResult Accept() => new(PeerBlockValidationStatus.Accepted, string.Empty);
    public static PeerBlockValidationResult AlreadyAccepted() => new(PeerBlockValidationStatus.AlreadyAccepted, string.Empty);
    public static PeerBlockValidationResult Pending(string reason) => new(PeerBlockValidationStatus.Pending, reason);
    public static PeerBlockValidationResult Reject(string reason) => new(PeerBlockValidationStatus.Rejected, reason);
}

public enum PeerBlockValidationStatus
{
    Accepted,
    AlreadyAccepted,
    Pending,
    Rejected
}
