using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Core.Constants;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node;
using Blockchain.Node.Services;
using Google.Protobuf;
using System.Net;
using System.Net.Http.Headers;

const string DefaultProjectId = "RaftSmokeProject";
const string DefaultUserName = "Alice";
const string DefaultPassword = "local-raft-producer-password";
const string DefaultDbPassword = "local-raft-db-password";
const string DefaultSyncToken = "local-raft-sync-token";

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    var options = CliOptions.Parse(args.Skip(1));
    return args[0].ToLowerInvariant() switch
    {
        "prepare" => Prepare(options),
        "submit" => await SubmitAsync(options),
        _ => UnknownCommand(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Prepare(CliOptions options)
{
    NetworkParameters.RequireProofOfWork = false;

    string dataRoot = options.GetRequired("data-root");
    string projectId = options.Get("project-id", DefaultProjectId);
    string userName = options.Get("user", DefaultUserName);
    string producerKeyPath = options.Get("producer-key-path", Path.Combine(dataRoot, "shared", "producer-key.dat"));
    string producerPassword = options.Get("producer-password", DefaultPassword);
    string dbPassword = options.Get("db-password", DefaultDbPassword);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(producerKeyPath))!);
    using var producerKey = LoadOrCreateProducerKey(producerKeyPath, producerPassword);
    string producerPublicKey = Convert.ToBase64String(producerKey.ExportSubjectPublicKeyInfo());
    var seedBlocks = CreateSeedBlocks(producerKey, producerPublicKey, projectId, userName);

    foreach (string nodeName in new[] { "node-a", "node-b" })
    {
        string dbPath = Path.Combine(dataRoot, nodeName, "node.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        SeedDatabase(dbPath, dbPassword, projectId, userName, producerPublicKey, seedBlocks);
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        producerKeyPath = Path.GetFullPath(producerKeyPath),
        producerPublicKey,
        projectId,
        userName
    }));
    return 0;
}

static async Task<int> SubmitAsync(CliOptions options)
{
    NetworkParameters.RequireProofOfWork = false;

    string dataRoot = options.GetRequired("data-root");
    string projectId = options.Get("project-id", DefaultProjectId);
    string userName = options.Get("user", DefaultUserName);
    string producerKeyPath = options.Get("producer-key-path", Path.Combine(dataRoot, "shared", "producer-key.dat"));
    string producerPassword = options.Get("producer-password", DefaultPassword);
    string dbPassword = options.Get("db-password", DefaultDbPassword);
    string syncToken = options.Get("sync-token", DefaultSyncToken);
    string submitUrl = options.Get("submit-url", "http://localhost:7042");
    string[] verifyUrls = options.Get("verify-urls", "http://localhost:7042,http://localhost:7043")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    using var producerKey = LoadOrCreateProducerKey(producerKeyPath, producerPassword);
    string producerPublicKey = Convert.ToBase64String(producerKey.ExportSubjectPublicKeyInfo());

    string dbPath = Path.Combine(dataRoot, "node-a", "node.db");
    var database = new DatabaseManager(dbPath, dbPassword);
    var latest = database.GetLatestBlock(projectId)
        ?? throw new InvalidOperationException($"Project chain '{projectId}' is not seeded.");

    var block = CreateSignedBlock(
        index: latest.Index + 1,
        previousHash: latest.Hash,
        channelId: projectId,
        data: JsonSerializer.Serialize(new
        {
            Type = "CodeCommit",
            ProjectId = projectId,
            User = userName,
            CommitHash = "grpc-smoke-" + Guid.NewGuid().ToString("N")[..12]
        }),
        signer: producerKey,
        publicKey: producerPublicKey);

    var scoreService = new ContributionScoreService(database, database);
    var selector = new ProducerSelector();
    var proposalFactory = new BlockProposalFactory(scoreService, selector);
    var proposal = proposalFactory.BuildImplicitProposal(block);
    if (!proposal.Accepted || proposal.Proposal == null)
    {
        throw new InvalidOperationException($"PoC proposal rejected before gRPC submit: {proposal.Reason}");
    }

    if (!string.Equals(proposal.Proposal.ContributionProof.ProducerPublicKey, producerPublicKey, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Seeded producer key was not selected by PoC.");
    }

    var model = GrpcProjectMapper.ToBlockModel(block);
    model.ContributionProof = GrpcProjectMapper.ToContributionProofModel(proposal.Proposal.ContributionProof);

    StatusReply? submitReply = null;
    string acceptedSubmitUrl = string.Empty;
    foreach (string candidateUrl in new[] { submitUrl }.Concat(verifyUrls).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var candidateReply = await SendGrpcWebUnaryAsync(
            candidateUrl,
            "ReceiveBlock",
            model,
            () => new StatusReply());
        if (candidateReply.Success)
        {
            submitReply = candidateReply;
            acceptedSubmitUrl = candidateUrl;
            break;
        }

        if (!candidateReply.Message.Contains("Raft leader is unavailable", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ReceiveBlock rejected proposal at {candidateUrl}: {candidateReply.Message}");
        }
    }

    if (submitReply == null)
    {
        throw new InvalidOperationException("ReceiveBlock was rejected by all candidate nodes because no contacted node accepted Raft leadership.");
    }

    await WaitForFollowersAsync(verifyUrls, projectId, syncToken, block.Hash);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        accepted = true,
        submitUrl = acceptedSubmitUrl,
        projectId,
        block.Index,
        block.Hash,
        submitReply.Message,
        verifiedUrls = verifyUrls
    }));
    return 0;
}

static async Task WaitForFollowersAsync(string[] urls, string projectId, string syncToken, string expectedHash)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var pending = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);

    while (pending.Count > 0 && !timeout.IsCancellationRequested)
    {
        foreach (string url in pending.ToArray())
        {
            var chain = await SendGrpcWebUnaryAsync(
                url,
                "GetChain",
                new ChainRequest
            {
                ChannelId = projectId,
                Count = 20,
                UserName = P2PNetworkService.NodeSyncUser,
                SyncToken = syncToken
            },
                () => new ChainResponse());

            if (chain.Blocks.Any(block => string.Equals(block.Hash, expectedHash, StringComparison.Ordinal)))
            {
                pending.Remove(url);
            }
        }

        if (pending.Count > 0)
        {
            await Task.Delay(1000, timeout.Token).ContinueWith(_ => { });
        }
    }

    if (pending.Count > 0)
    {
        throw new InvalidOperationException($"Committed block did not appear on: {string.Join(", ", pending)}");
    }
}

static async Task<TResponse> SendGrpcWebUnaryAsync<TRequest, TResponse>(
    string baseUrl,
    string method,
    TRequest request,
    Func<TResponse> responseFactory)
    where TRequest : IMessage
    where TResponse : IMessage
{
    using var httpClient = new HttpClient
    {
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    string url = $"{baseUrl.TrimEnd('/')}/blockchain.BlockchainService/{method}";
    using var content = new ByteArrayContent(CreateGrpcFrame(request.ToByteArray()));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc-web+proto");
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = content,
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
    httpRequest.Headers.Add("x-grpc-web", "1");
    httpRequest.Headers.Add("x-user-agent", "nexus-raft-grpc-smoke");
    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/grpc-web+proto"));

    using var response = await httpClient.SendAsync(httpRequest);
    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"gRPC-Web {method} HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(responseBytes)}");
    }

    foreach (byte[] frame in ReadGrpcDataFrames(responseBytes))
    {
        var message = responseFactory();
        message.MergeFrom(frame);
        return message;
    }

    throw new InvalidOperationException($"gRPC-Web {method} returned no protobuf data frame.");
}

static byte[] CreateGrpcFrame(byte[] payload)
{
    var framed = new byte[payload.Length + 5];
    framed[0] = 0;
    framed[1] = (byte)((payload.Length >> 24) & 0xFF);
    framed[2] = (byte)((payload.Length >> 16) & 0xFF);
    framed[3] = (byte)((payload.Length >> 8) & 0xFF);
    framed[4] = (byte)(payload.Length & 0xFF);
    Buffer.BlockCopy(payload, 0, framed, 5, payload.Length);
    return framed;
}

static IEnumerable<byte[]> ReadGrpcDataFrames(byte[] responseBytes)
{
    int offset = 0;
    while (offset + 5 <= responseBytes.Length)
    {
        byte flags = responseBytes[offset];
        int length =
            (responseBytes[offset + 1] << 24) |
            (responseBytes[offset + 2] << 16) |
            (responseBytes[offset + 3] << 8) |
            responseBytes[offset + 4];
        offset += 5;

        if (length < 0 || offset + length > responseBytes.Length)
        {
            yield break;
        }

        bool trailerFrame = (flags & 0x80) != 0;
        if (!trailerFrame)
        {
            var payload = new byte[length];
            Buffer.BlockCopy(responseBytes, offset, payload, 0, length);
            yield return payload;
        }

        offset += length;
    }
}

static IReadOnlyList<Block> CreateSeedBlocks(ECDsa signer, string publicKey, string projectId, string userName)
{
    var genesis = CreateSignedBlock(
        0,
        "0",
        projectId,
        JsonSerializer.Serialize(new
        {
            Type = "CreateProject",
            ProjectId = projectId,
            User = userName
        }),
        signer,
        publicKey);
    var contribution = CreateSignedBlock(
        1,
        genesis.Hash,
        projectId,
        JsonSerializer.Serialize(new
        {
            Type = "CodeCommit",
            ProjectId = projectId,
            User = userName,
            CommitHash = "seed-contribution"
        }),
        signer,
        publicKey);
    return new[] { genesis, contribution };
}

static void SeedDatabase(
    string dbPath,
    string dbPassword,
    string projectId,
    string userName,
    string publicKey,
    IReadOnlyList<Block> seedBlocks)
{
    var database = new DatabaseManager(dbPath, dbPassword);
    if (database.LoadChain(projectId).Count < seedBlocks.Count)
    {
        for (int i = 0; i < seedBlocks.Count; i++)
        {
            var block = seedBlocks[i];
            database.SaveBlock(block, projectId);
            database.SaveFinalityMetadata(new BlockFinalityMetadata(
                block.Hash,
                projectId,
                ConsensusFinalityModes.Raft,
                i,
                null,
                DateTime.UtcNow));
        }
    }

    database.SaveUserPublicKey(userName, publicKey);
}

static Block CreateSignedBlock(
    int index,
    string previousHash,
    string channelId,
    string data,
    ECDsa signer,
    string publicKey)
{
    var timestamp = DateTimeOffset.UtcNow;
    var block = new Block
    {
        Index = index,
        PreviousHash = previousHash,
        ChannelId = channelId,
        Data = data,
        Timestamp = timestamp.UtcDateTime,
        TimestampUnixSeconds = timestamp.ToUnixTimeSeconds(),
        ValidatorPublicKey = publicKey
    };

    string signableData = $"{block.Index}{block.TimestampUnixSeconds}{block.Data}{block.PreviousHash}";
    block.Signature = Convert.ToBase64String(signer.SignData(
        Encoding.UTF8.GetBytes(signableData),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    new BlockMiner().Mine(block);
    return block;
}

static ECDsa LoadOrCreateProducerKey(string keyPath, string password)
{
    var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    if (File.Exists(keyPath))
    {
        ecdsa.ImportEncryptedPkcs8PrivateKey(
            Encoding.UTF8.GetBytes(password),
            File.ReadAllBytes(keyPath),
            out _);
        return ecdsa;
    }

    byte[] encryptedBytes = ecdsa.ExportEncryptedPkcs8PrivateKey(
        Encoding.UTF8.GetBytes(password),
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100000));
    File.WriteAllBytes(keyPath, encryptedBytes);
    return ecdsa;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintUsage();
    return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  RaftGrpcSmoke prepare --data-root <path> [--producer-key-path <path>]");
    Console.Error.WriteLine("  RaftGrpcSmoke submit --data-root <path> [--submit-url http://localhost:7042] [--verify-urls http://localhost:7042,http://localhost:7043]");
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values;

    private CliOptions(Dictionary<string, string> values) => _values = values;

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                key = arg[2..];
                values[key] = "true";
                continue;
            }

            if (key == null)
            {
                throw new InvalidOperationException($"Unexpected argument '{arg}'.");
            }

            values[key] = arg;
            key = null;
        }

        return new CliOptions(values);
    }

    public string GetRequired(string name)
    {
        string value = Get(name, string.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required option --{name}.");
        }

        return value;
    }

    public string Get(string name, string defaultValue) =>
        _values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;
}
