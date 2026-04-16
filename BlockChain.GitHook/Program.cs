using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Blockchain.Node;

using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;

Console.WriteLine("[BlockChain Git Hook] Инициализация якорения коммита...");

try
{
    string keyFilePath = ".git/hooks/my_keys.json";
    string myPubKey = "System"; 
    string myPrivKeyHex = "";

    if (File.Exists(keyFilePath))
    {
        var keys = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(keyFilePath));
        myPubKey = keys.GetProperty("publicKey").GetString() ?? "System";
        myPrivKeyHex = keys.GetProperty("privateKeyHex").GetString() ?? "";
    }
    else
    {
        Console.WriteLine("[BlockChain Git Hook] Внимание: Файл my_keys.json не найден. Коммит пойдет от имени 'System'.");
    }

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

    long nonce = 0;
    string hash = "";
    string targetPrefix = "000";

    using var sha256 = SHA256.Create();
    while (true)
    {
        string rawData = $"{prevHash}{timestamp}{jsonPayload}{myPubKey}{nonce}";
        byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        hash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        if (hash.StartsWith(targetPrefix))
        {
            break;
        }
        nonce++;
    }

    Console.WriteLine($"[BlockChain Git Hook] Блок найден! Nonce: {nonce}, Hash: {hash[..15]}...");

    string signature = "";
    if (!string.IsNullOrEmpty(myPrivKeyHex))
    {
        signature = SignData(jsonPayload, myPrivKeyHex);
    }

    var block = new BlockModel
    {
        Index = expectedIndex,
        Timestamp = timestamp,
        Data = jsonPayload,
        PreviousHash = prevHash,
        Hash = hash,
        ValidatorPublicKey = myPubKey,
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

static string SignData(string data, string privKeyHex)
{
    try
    {
        var privateKeyBytes = Convert.FromHexString(privKeyHex);
        var curve = SecNamedCurves.GetByName("secp256r1");
        var domainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var privKeyParams = new ECPrivateKeyParameters(new BigInteger(1, privateKeyBytes), domainParams);

        var signer = new ECDsaSigner();
        signer.Init(true, privKeyParams);

        var digest = new Sha256Digest();
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        digest.BlockUpdate(dataBytes, 0, dataBytes.Length);
        byte[] hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);

        var sig = signer.GenerateSignature(hash);
        byte[] r = sig[0].ToByteArrayUnsigned();
        byte[] s = sig[1].ToByteArrayUnsigned();

        byte[] result = new byte[64];
        Buffer.BlockCopy(r, 0, result, 32 - r.Length, r.Length);
        Buffer.BlockCopy(s, 0, result, 64 - s.Length, s.Length);

        return Convert.ToBase64String(result);
    }
    catch (Exception)
    {
        return "";
    }
}