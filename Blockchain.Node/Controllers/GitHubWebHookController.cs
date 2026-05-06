using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Blockchain.Node.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class GitHubWebhookController : ControllerBase
    {
        private readonly Channel<string> _commitQueue;
        private readonly string _webhookSecret;

        public GitHubWebhookController(Channel<string> commitQueue, IConfiguration config)
        {
            _commitQueue = commitQueue;
            _webhookSecret = config["WebhookSecret"]
                ?? throw new InvalidOperationException("Webhook secret is not configured in appsettings.json");
        }

        [HttpPost("git")]
        public async Task<IActionResult> ReceiveGitHubWebhook()
        {
            if (!Request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader))
            {
                return Unauthorized("Missing X-Hub-Signature-256 header");
            }

            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            string payload = await reader.ReadToEndAsync();

            if (!VerifySignature(payload, signatureHeader))
            {
                Console.WriteLine("[Security] ❌ Unauthorized webhook invocation attempt! Signature mismatch.");
                return Unauthorized("Invalid signature");
            }

            await _commitQueue.Writer.WriteAsync(payload);

            return Accepted();
        }

        private bool VerifySignature(string payload, string signatureHeader)
        {
            string headerValue = signatureHeader.ToString();
            if (!headerValue.StartsWith("sha256=")) return false;

            string providedSignature = headerValue.Substring(7);

            byte[] secretBytes = Encoding.UTF8.GetBytes(_webhookSecret);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

            using var hmac = new HMACSHA256(secretBytes);
            byte[] hashBytes = hmac.ComputeHash(payloadBytes);
            string computedSignature = Convert.ToHexString(hashBytes).ToLowerInvariant();

            return string.Equals(providedSignature, computedSignature, StringComparison.OrdinalIgnoreCase);
        }
    }
}