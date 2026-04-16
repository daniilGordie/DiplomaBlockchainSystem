using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Blockchain.Node;

Console.WriteLine("[BlockChain Git Hook] Инициализация якорения коммита...");

try
{
   
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "log -1 --format=\"%H|%an|%s\"", 
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    string gitOutput = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (string.IsNullOrWhiteSpace(gitOutput))
    {
        Console.WriteLine("[BlockChain Git Hook] Ошибка: Не удалось получить данные Git.");
        return 0; 
    }

    var parts = gitOutput.Trim().Split('|', 3);
    string commitHash = parts[0];
    string author = parts[1];
    string message = parts[2];

    string channelId = "Alpha"; 

 
    var payload = new
    {
        Type = "CodeCommit",
        User = author,
        ProjectId = channelId,
        Repository = "local-repo", 
        CommitHash = commitHash,
        Message = message,
        Timestamp = DateTime.UtcNow.ToString("O")
    };

    string jsonPayload = JsonSerializer.Serialize(payload);

    using var channel = GrpcChannel.ForAddress("http://localhost:5041");
    var client = new BlockchainService.BlockchainServiceClient(channel);

    var chainResp = await client.GetChainAsync(new ChainRequest
    {
        Count = 1,
        ChannelId = channelId,
        UserName = author
    });

    string prevHash = chainResp.Blocks.Count > 0 ? chainResp.Blocks.Last().Hash : "0";
    int expectedIndex = chainResp.Blocks.Count > 0 ? chainResp.Blocks.Last().Index + 1 : 0;

    Console.WriteLine($"[BlockChain Git Hook] Вычисление PoW для канала {channelId}...");

    string timestamp = DateTime.UtcNow.ToString("O");

    string pubKey = "DEV_PUBLIC_KEY_PLACEHOLDER";
    string signature = "DEV_SIGNATURE_PLACEHOLDER";

    long nonce = 0;
    string hash = "";
    string targetPrefix = "000"; 

    using var sha256 = SHA256.Create();
    while (true)
    {
        string rawData = $"{prevHash}{timestamp}{jsonPayload}{pubKey}{nonce}";
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        hash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        if (hash.StartsWith(targetPrefix))
        {
            break;
        }
        nonce++;
    }

    Console.WriteLine($"[BlockChain Git Hook] Блок найден! Nonce: {nonce}, Hash: {hash[..15]}...");

  
    var block = new BlockModel
    {
        Index = expectedIndex,
        Timestamp = timestamp,
        Data = jsonPayload,
        PreviousHash = prevHash,
        Hash = hash,
        ValidatorPublicKey = pubKey,
        Signature = signature,
        Nonce = nonce,
        ChannelId = channelId
    };

    var reply = await client.ReceiveBlockAsync(block);

    if (reply.Success)
    {
        Console.WriteLine($"[BlockChain Git Hook] ✅ Коммит успешно заякорен в блокчейн: {reply.Message}");
    }
    else
    {
        Console.WriteLine($"[BlockChain Git Hook] ❌ Блок отклонен смарт-контрактом: {reply.Message}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[BlockChain Git Hook] ⚠️ Ошибка синхронизации с блокчейном: {ex.Message}");
}

return 0; 