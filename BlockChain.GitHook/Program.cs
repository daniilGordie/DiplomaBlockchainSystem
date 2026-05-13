using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;

class Program
{
    private const string PendingQueueFile = "pending_hooks.json";

    static async Task<int> Main(string[] args)
    {
        var config = LoadSettings();

        string webhookSecret = Environment.GetEnvironmentVariable("NEXUS_WEBHOOK_SECRET")
            ?? GetSetting(config, "WebhookSecret")
            ?? throw new InvalidOperationException("Critical Error: 'WebhookSecret' was not found in appsettings.json");
        string nodeWebhookUrl = Environment.GetEnvironmentVariable("NEXUS_NODE_WEBHOOK_URL")
            ?? GetSetting(config, "NodeWebhookUrl")
            ?? "https://localhost:7066/api/webhooks/git";
        string ipfsApiUrl = Environment.GetEnvironmentVariable("NEXUS_IPFS_API_URL")
            ?? GetSetting(config, "IpfsApiUrl")
            ?? "http://127.0.0.1:5001/api/v0/add";
        string projectId = Environment.GetEnvironmentVariable("NEXUS_PROJECT_ID")
            ?? GetSetting(config, "ProjectId")
            ?? "System";
        string repository = Environment.GetEnvironmentVariable("NEXUS_REPOSITORY")
            ?? GetSetting(config, "Repository")
            ?? new DirectoryInfo(Environment.CurrentDirectory).Name;

        if (string.IsNullOrWhiteSpace(webhookSecret))
        {
            throw new InvalidOperationException("WebhookSecret is not configured. Use NEXUS_WEBHOOK_SECRET or appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("ProjectId is not configured. Use NEXUS_PROJECT_ID or appsettings.json.");
        }

        Console.WriteLine("[Nexus Git Hook] Initializing commit intercept...");

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
                Console.WriteLine("[Nexus Git Hook] ❌ Error: Failed to retrieve Git data.");
                return 1;
            }

            var parts = gitOutput.Trim().Split('|', 3);
            string commitHash = parts[0];
            string author = parts[1];
            string message = parts[2];

            Console.WriteLine($"[Nexus Git Hook] Commit detected: {commitHash[..7]} by {author}");

            string patchCid = "";
            var diffProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"show {commitHash} --pretty=format: --unified=3",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            diffProcess.Start();
            string diffText = await diffProcess.StandardOutput.ReadToEndAsync();
            await diffProcess.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(diffText))
            {
                using var httpClientIpfs = new HttpClient();
                using var content = new MultipartFormDataContent();
                content.Add(new StringContent(diffText), "file", "diff.patch");
                try
                {
                    var ipfsRes = await httpClientIpfs.PostAsync(ipfsApiUrl, content);
                    if (ipfsRes.IsSuccessStatusCode)
                    {
                        var ipfsJson = await ipfsRes.Content.ReadAsStringAsync();
                        patchCid = JsonDocument.Parse(ipfsJson).RootElement.GetProperty("Hash").GetString() ?? "";
                        Console.WriteLine($"[Nexus Git Hook] Diff successfully uploaded to IPFS. CID: {patchCid[..7]}...");
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("[Nexus Git Hook] ⚠️ Warning: Local IPFS node is unreachable. Diff skipped.");
                }
            }

            var intent = new HookIntent
            {
                Repository = repository,
                CommitHash = commitHash,
                Message = message,
                Author = author,
                PatchCid = patchCid,
                ProjectId = projectId
            };

            var queue = LoadQueue();
            if (!queue.Exists(x => string.Equals(x.CommitHash, intent.CommitHash, StringComparison.OrdinalIgnoreCase)))
            {
                queue.Add(intent);
            }
            SaveQueue(queue);

            using var clientNode = new HttpClient();
            await FlushQueueAsync(queue, clientNode, nodeWebhookUrl, webhookSecret);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Nexus Git Hook] ⚠️ Critical error occurred: {ex.Message}");
        }

        return 0;
    }

    private static async Task FlushQueueAsync(List<HookIntent> queue, HttpClient clientNode, string nodeWebhookUrl, string webhookSecret)
    {
        var stillPending = new List<HookIntent>();

        foreach (var intent in queue)
        {
            bool sent = false;
            int attempts = 0;
            TimeSpan delay = TimeSpan.FromMilliseconds(400);

            while (!sent && attempts < 4)
            {
                attempts++;
                try
                {
                    string jsonPayload = JsonSerializer.Serialize(intent);
                    string signature = ComputeHmacSha256(jsonPayload, webhookSecret);

                    using var request = new HttpRequestMessage(HttpMethod.Post, nodeWebhookUrl);
                    request.Headers.Add("X-Hub-Signature-256", $"sha256={signature}");
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    var response = await clientNode.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        sent = true;
                        Console.WriteLine($"[Nexus Git Hook] ✅ Delivered commit {intent.CommitHash[..7]} to node.");
                        Console.WriteLine("[Nexus Git Hook] Commit will be anchored by the node oracle.");
                    }
                    else
                    {
                        Console.WriteLine($"[Nexus Git Hook] attempt {attempts}/4 failed, status {(int)response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Nexus Git Hook] attempt {attempts}/4 failed: {ex.Message}");
                }

                if (!sent && attempts < 4)
                {
                    await Task.Delay(delay);
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
                }
            }

            if (!sent) stillPending.Add(intent);
        }

        SaveQueue(stillPending);
    }

    private static List<HookIntent> LoadQueue()
    {
        try
        {
            if (!File.Exists(PendingQueueFile)) return new List<HookIntent>();
            var json = File.ReadAllText(PendingQueueFile);
            return JsonSerializer.Deserialize<List<HookIntent>>(json) ?? new List<HookIntent>();
        }
        catch
        {
            return new List<HookIntent>();
        }
    }

    private static void SaveQueue(List<HookIntent> queue)
    {
        var json = JsonSerializer.Serialize(queue, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PendingQueueFile, json);
    }

    private class HookIntent
    {
        public string Repository { get; set; } = "";
        public string CommitHash { get; set; } = "";
        public string Message { get; set; } = "";
        public string Author { get; set; } = "";
        public string PatchCid { get; set; } = "";
        public string ProjectId { get; set; } = "System";
    }

    private static Dictionary<string, string> LoadSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            settings[property.Name] = property.Value.GetString() ?? "";
        }

        return settings;
    }

    private static string? GetSetting(Dictionary<string, string> settings, string key)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string ComputeHmacSha256(string payload, string secret)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(secretBytes);
        byte[] hashBytes = hmac.ComputeHash(payloadBytes);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
