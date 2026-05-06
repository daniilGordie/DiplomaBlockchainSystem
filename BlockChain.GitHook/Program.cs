using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // Extracting secret for webhook signing
        string webhookSecret = config["WebhookSecret"]
            ?? throw new InvalidOperationException("Critical Error: 'WebhookSecret' was not found in appsettings.json");

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
                    var ipfsRes = await httpClientIpfs.PostAsync("http://127.0.0.1:5001/api/v0/add", content);
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

            var intent = new
            {
                Repository = "local-repo",
                CommitHash = commitHash,
                Message = message,
                Author = author,
                PatchCid = patchCid
            };

            using var clientNode = new HttpClient();

            string jsonPayload = JsonSerializer.Serialize(intent);
            string signature = ComputeHmacSha256(jsonPayload, webhookSecret);

            clientNode.DefaultRequestHeaders.Add("X-Hub-Signature-256", $"sha256={signature}");

            using var jsonContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await clientNode.PostAsync("https://localhost:7066/api/webhooks/git", jsonContent);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("[Nexus Git Hook] ✅ Notification successfully sent to the node.");
                Console.WriteLine("[Nexus Git Hook] 🔔 Please open the Nexus dashboard for cryptographic signing!");
            }
            else
            {
                Console.WriteLine($"[Nexus Git Hook] ❌ Error communicating with Node Broker. Status Code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Nexus Git Hook] ⚠️ Critical error occurred: {ex.Message}");
        }

        return 0;
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