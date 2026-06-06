using System.Security.Cryptography;

var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "help";
if (command is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

if (command != "setup")
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 2;
}

string mode = GetPositional(args, 1, "full-node").Trim().ToLowerInvariant();
if (mode is "full" or "fullnode")
{
    mode = "full-node";
}

if (mode is not ("bootstrap" or "full-node"))
{
    Console.Error.WriteLine("Mode must be bootstrap or full-node.");
    return 2;
}

var options = ParseOptions(args.Skip(2));
string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string deployDir = Path.Combine(repoRoot, "deploy");
string outputPath = GetOption(options, "output")
    ?? Path.Combine(deployDir, mode == "bootstrap" ? "bootstrap-node.env" : "full-node.env");
string templatePath = Path.Combine(deployDir, mode == "bootstrap" ? "bootstrap-node.env.example" : "full-node.env.example");

if (!File.Exists(templatePath))
{
    Console.Error.WriteLine($"Template not found: {templatePath}");
    return 1;
}

if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0 && !HasFlag(options, "force"))
{
    Console.Error.WriteLine($"Refusing to overwrite existing config: {outputPath}");
    Console.Error.WriteLine("Pass --force to overwrite.");
    return 1;
}

var lines = File.ReadAllLines(templatePath).ToList();
SetValue(lines, "NODE_DB_PASSWORD", NewSecret());
SetValue(lines, "NODE_ADMIN_TOKEN", NewSecret());
SetValue(lines, "WEBHOOK_SECRET", NewSecret());
SetValue(lines, "P2P_SYNC_TOKEN", NewSecret());
SetValue(lines, "P2P_REGISTRATION_TOKEN", string.Empty);
SetValue(lines, "P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK", "false");

string nodeId = GetOption(options, "node-id") ?? $"{(mode == "bootstrap" ? "bootstrap" : "full")}-{Guid.NewGuid():N}"[..13];
SetValue(lines, "P2P_NODE_ID", nodeId);

if (mode == "bootstrap")
{
    string? publicUrl = GetOption(options, "public-url");
    if (!string.IsNullOrWhiteSpace(publicUrl))
    {
        SetValue(lines, "P2P_PUBLIC_URL", NormalizeUrl(publicUrl));
    }
}
else
{
    SetValue(lines, "IROH_LOCAL_API_TOKEN", NewSecret());
    string? bootstrapGrpcUrl = GetOption(options, "bootstrap");
    if (!string.IsNullOrWhiteSpace(bootstrapGrpcUrl))
    {
        SetValue(lines, "P2P_BOOTSTRAP_GRPC_URL", NormalizeUrl(bootstrapGrpcUrl));
    }
}

string? oraclePublicKey = GetOption(options, "oracle-public-key");
if (!string.IsNullOrWhiteSpace(oraclePublicKey))
{
    SetValue(lines, "ORACLE_PUBLIC_KEY", oraclePublicKey.Trim());
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllLines(outputPath, lines);

Console.WriteLine($"Created {outputPath}");
Console.WriteLine("Review public URLs and ORACLE_PUBLIC_KEY before starting Docker Compose.");
Console.WriteLine(mode == "bootstrap"
    ? "Start: docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up -d --build"
    : "Start: docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml up -d --build");
return 0;

static void PrintHelp()
{
    Console.WriteLine("""
Nexus.Setup

Usage:
  dotnet run --project Nexus.Setup -- setup bootstrap --public-url https://bootstrap.example.com
  dotnet run --project Nexus.Setup -- setup full-node --bootstrap https://bootstrap.example.com:7141

Options:
  --output <path>             Output .env path.
  --force                     Overwrite existing output file.
  --node-id <id>              Stable node id to write.
  --public-url <url>          Bootstrap public URL.
  --bootstrap <url>           Full-node bootstrap gRPC URL.
  --oracle-public-key <key>   Oracle public key value.
""");
}

static Dictionary<string, string?> ParseOptions(IEnumerable<string> tokens)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    string? pending = null;
    foreach (string token in tokens)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            pending = token[2..];
            result[pending] = "true";
            continue;
        }

        if (pending != null)
        {
            result[pending] = token;
            pending = null;
        }
    }

    return result;
}

static string? GetOption(IReadOnlyDictionary<string, string?> options, string name) =>
    options.TryGetValue(name, out var value) && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        ? value
        : null;

static bool HasFlag(IReadOnlyDictionary<string, string?> options, string name) =>
    options.TryGetValue(name, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

static string GetPositional(string[] args, int index, string fallback) => args.Length > index ? args[index] : fallback;

static string NewSecret()
{
    Span<byte> buffer = stackalloc byte[32];
    RandomNumberGenerator.Fill(buffer);
    return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static void SetValue(List<string> lines, string name, string value)
{
    for (int i = 0; i < lines.Count; i++)
    {
        if (lines[i].StartsWith($"{name}=", StringComparison.Ordinal))
        {
            lines[i] = $"{name}={value}";
            return;
        }
    }

    lines.Add($"{name}={value}");
}

static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');

static string FindRepoRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory != null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DiplomaBlockchainSystem.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}
