using System.Security.Cryptography;
using Blockchain.Node;
using Blockchain.UI.Application.Security;
using Blockchain.UI.Services;

namespace Blockchain.UI.Application.Clients;

public sealed class BlockAnchoringService : IBlockAnchoringService
{
    private readonly BlockchainService.BlockchainServiceClient _blockchainClient;
    private readonly IReadRequestAuthorizer _readAuthorizer;
    private readonly KeyService _keyService;

    public BlockAnchoringService(
        BlockchainService.BlockchainServiceClient blockchainClient,
        IReadRequestAuthorizer readAuthorizer,
        KeyService keyService)
    {
        _blockchainClient = blockchainClient;
        _readAuthorizer = readAuthorizer;
        _keyService = keyService;
    }

    public async Task<UserNameAvailabilityResult> CheckUserNameAvailabilityAsync(string userName)
    {
        var response = await _blockchainClient.CheckUserNameAsync(new UserNameRequest
        {
            UserName = userName?.Trim() ?? string.Empty
        });

        return new UserNameAvailabilityResult(
            response.Success,
            response.Exists,
            response.Message);
    }

    public async Task<UserIdentityCheckResult> CheckCurrentUserIdentityAsync()
    {
        var request = new UserRequest
        {
            UserName = _keyService.UserName,
            UserPublicKey = _keyService.PublicKey ?? ""
        };

        _readAuthorizer.Apply(request, $"USER:{_keyService.UserName}:IDENTITY");
        var response = await _blockchainClient.GetUserIdentityAsync(request);
        return new UserIdentityCheckResult(
            response.Success,
            response.Exists,
            response.PublicKeyMatches,
            response.Message);
    }

    public async Task<BlockAnchorResult> AnchorJsonStringAsync(string json, string targetChannel)
    {
        return await AnchorSerializedAsync(
            json,
            targetChannel,
            signableData => Task.FromResult(_keyService.SignData(signableData)),
            "Success. Block anchored via PoC.",
            "Rejected");
    }

    public async Task<BlockAnchorResult> AnchorJsonStringWithKeystoreAsync(
        string json,
        string targetChannel,
        string keystore,
        string password)
    {
        return await AnchorSerializedAsync(
            json,
            targetChannel,
            signableData => SignWithKeystoreAsync(keystore, password, signableData),
            "Work item successfully written to the blockchain.",
            "Node error");
    }

    private async Task<BlockAnchorResult> AnchorSerializedAsync(
        string json,
        string targetChannel,
        Func<string, Task<string>> signAsync,
        string successMessage,
        string failurePrefix)
    {
        var latestBlock = await LoadLatestAnchorBlockAsync(targetChannel);
        string prevHash = latestBlock != null ? latestBlock.Hash : "0";
        int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
        string timestamp = DateTime.UtcNow.ToString("O");
        string publicKey = _keyService.PublicKey ?? "";
        string signableData = $"{expectedIndex}{timestamp}{json}{prevHash}";
        string signature = await signAsync(signableData);

        var block = new BlockModel
        {
            Index = expectedIndex,
            Data = json,
            Timestamp = timestamp,
            PreviousHash = prevHash,
            Hash = ComputeSimpleHash(signableData),
            ValidatorPublicKey = publicKey,
            Signature = signature,
            ChannelId = targetChannel
        };

        await MineBlockLocal(block);

        var response = await _blockchainClient.ReceiveBlockAsync(block);
        return response.Success
            ? new BlockAnchorResult(true, successMessage)
            : new BlockAnchorResult(false, $"{failurePrefix}: {response.Message}");
    }

    private async Task<BlockModel?> LoadLatestAnchorBlockAsync(string targetChannel)
    {
        if (string.Equals(targetChannel, "System", StringComparison.OrdinalIgnoreCase))
        {
            var systemLatest = await _blockchainClient.GetLastBlockAsync(new EmptyRequest());
            return string.IsNullOrWhiteSpace(systemLatest.Hash) || systemLatest.Hash == "0"
                ? null
                : systemLatest;
        }

        var chainRequest = new ChainRequest
        {
            Count = 1,
            ChannelId = targetChannel,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? "",
            AfterIndex = -1
        };
        _readAuthorizer.Apply(chainRequest, $"CHAIN:{targetChannel}");
        var chainResponse = await _blockchainClient.GetChainAsync(chainRequest);

        return chainResponse.Blocks.Count > 0 ? chainResponse.Blocks.Last() : null;
    }

    private Task<string> SignWithKeystoreAsync(string keystore, string password, string signableData)
    {
        if (keystore.Contains("\"ProtectionMode\":\"passkey\"", StringComparison.OrdinalIgnoreCase))
        {
            return _keyService.SignDataWithPasskeyKeystoreAsync(keystore, signableData);
        }

        return Task.FromResult(_keyService.SignDataWithKeystore(keystore, password, signableData));
    }

    private static string ComputeSimpleHash(string data)
    {
        using var sha256 = SHA256.Create();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static async Task MineBlockLocal(BlockModel block)
    {
        await Task.Delay(10);

        DateTime timestamp = DateTime.Parse(block.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        string timeStringS = timestamp.ToString("s");

        block.Nonce = 0;
        using var sha256 = SHA256.Create();
        string baseData = $"{block.Index}{timeStringS}{block.Data}{block.PreviousHash}{block.ValidatorPublicKey}{block.Signature}";

        while (true)
        {
            block.Nonce++;
            string rawData = baseData + block.Nonce;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawData);
            byte[] hashBytes = sha256.ComputeHash(bytes);
            block.Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (block.Hash.StartsWith("000", StringComparison.Ordinal))
            {
                break;
            }

            if (block.Nonce % 500 == 0)
            {
                await Task.Yield();
            }
        }
    }
}
