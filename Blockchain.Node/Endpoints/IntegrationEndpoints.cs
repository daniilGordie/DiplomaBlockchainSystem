using System.Text;
using System.Text.Json;
using Blockchain.Application.Artifacts;
using Blockchain.Application.Git;
using Blockchain.Node.Services;

namespace Blockchain.Node.Endpoints;

public static class IntegrationEndpoints
{
    public static WebApplication MapGitIntegrationEndpoints(this WebApplication app, string webhookSecret)
    {
        app.MapPost("/api/webhooks/git", async (
            HttpRequest request,
            IConfiguration configuration,
            ProjectEventAnchorService anchorService,
            AnchorGitCommitUseCase useCase) =>
        {
            if (!request.Headers.TryGetValue("X-Hub-Signature-256", out var signatureHeader))
            {
                Console.WriteLine("[Security] Reject: Header is missing X-Hub-Signature-256.");
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            string payload = await reader.ReadToEndAsync();

            if (!GitWebhookSecurity.IsValidSignature(payload, webhookSecret, signatureHeader.ToString()))
            {
                Console.WriteLine("[Security] Reject: Wrong crypto webhook sign.");
                return Results.Unauthorized();
            }

            string defaultProjectId =
                configuration["GitWebhookDefaultProjectId"]
                ?? Environment.GetEnvironmentVariable("NEXUS_PROJECT_ID")
                ?? string.Empty;

            var intent = ParseGitIntent(payload, request, defaultProjectId);
            if (intent == null)
            {
                return Results.BadRequest("Invalid git payload. Expected Nexus hook payload or supported Git provider push payload.");
            }

            var result = useCase.Execute(new AnchorGitCommitCommand(
                intent.Repository,
                intent.CommitHash,
                intent.Message,
                intent.Author,
                intent.PatchCid,
                intent.ProjectId,
                intent.Provider,
                intent.Branch));

            if (result.Status == AnchorGitCommitStatus.DuplicateIgnored)
            {
                return Results.Ok(new { status = "duplicate_ignored" });
            }

            if (!result.ShouldAnchor || result.Payload == null)
            {
                return Results.BadRequest(result.Message);
            }

            var anchor = await anchorService.AnchorAsync(result.ProjectId, result.Payload, result.Payload.Timestamp);
            if (!anchor.Accepted)
            {
                return Results.BadRequest(new { status = "rejected", reason = anchor.Error });
            }

            return Results.Ok(new { status = "anchored", blockHash = anchor.BlockHash, channelId = anchor.ChannelId });
        });

        app.MapPost("/api/integrations/git/connect", (
            GitRepositoryConnectRequest intent,
            ConnectGitRepositoryUseCase useCase) =>
        {
            var result = useCase.Execute(new ConnectGitRepositoryCommand(
                intent.ProjectId,
                intent.Repository,
                intent.User,
                intent.Timestamp,
                intent.UserPublicKey,
                intent.UserSignature));

            if (result.Success)
            {
                return Results.Ok(new
                {
                    status = "connected",
                    repository = result.Repository,
                    projectId = result.ProjectId
                });
            }

            return result.Status switch
            {
                ConnectGitRepositoryStatus.Unauthorized => Results.Unauthorized(),
                ConnectGitRepositoryStatus.Forbidden => Results.Forbid(),
                ConnectGitRepositoryStatus.BindingConflict => Results.BadRequest(result.Message),
                ConnectGitRepositoryStatus.DuplicateRequest => Results.BadRequest(result.Message),
                _ => Results.BadRequest(result.Message)
            };
        });

        app.MapGet("/api/integrations/git/status", (IConfiguration configuration, GitProjectBindingStore bindingStore) =>
        {
            string publicUrl = configuration["P2P:PublicUrl"] ?? string.Empty;
            string defaultProjectId =
                configuration["GitWebhookDefaultProjectId"]
                ?? Environment.GetEnvironmentVariable("NEXUS_PROJECT_ID")
                ?? string.Empty;
            var bindings = bindingStore.GetBindingsSnapshot();
            string webhookUrl = string.IsNullOrWhiteSpace(publicUrl)
                ? "/api/webhooks/git"
                : $"{publicUrl.TrimEnd('/')}/api/webhooks/git";

            return Results.Ok(new
            {
                integration = "git",
                mode = "trusted-oracle-auto-anchor",
                webhookUrl,
                source = "GitEvent",
                providers = new[] { "NexusGitHook", "GitHubPush" },
                acceptsPayloads = new[] { "NexusGitHook", "GitHubPush" },
                defaultProjectId = string.IsNullOrWhiteSpace(defaultProjectId) ? "(not set)" : defaultProjectId,
                bindingPolicy = "one-repository-per-project",
                bindingsCount = bindings.Count,
                bindings = bindings.Select(x => new { repository = x.Key, projectId = x.Value }).ToArray(),
                webhookSecretConfigured = !string.IsNullOrWhiteSpace(configuration["WebhookSecret"]),
                projectIdSource = "payload.projectId or ?projectId=... or GitWebhookDefaultProjectId/NEXUS_PROJECT_ID",
                queueFile = "pending_hooks.json"
            });
        });

        return app;
    }

    public static WebApplication MapArtifactIntegrationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/integrations/artifacts/register", async (
            ArtifactAnchorIntent intent,
            ProjectEventAnchorService anchorService,
            AnchorArtifactUseCase useCase) =>
        {
            var result = useCase.Execute(new AnchorArtifactCommand(
                intent.ProjectId,
                intent.FileHash,
                intent.FileName,
                intent.SizeBytes,
                intent.ContentType,
                intent.User,
                intent.RegisteredBy,
                intent.VerificationMethod,
                intent.Timestamp,
                intent.UserPublicKey,
                intent.UserSignature));

            if (result.Status == AnchorArtifactStatus.DuplicateIgnored)
            {
                return Results.Ok(new { status = "duplicate_ignored" });
            }

            if (!result.ShouldAnchor || result.Payload == null)
            {
                return result.Status switch
                {
                    AnchorArtifactStatus.Unauthorized => Results.Unauthorized(),
                    AnchorArtifactStatus.Forbidden => Results.Forbid(),
                    _ => Results.BadRequest(result.Message)
                };
            }

            var anchor = await anchorService.AnchorAsync(result.ProjectId, result.Payload, result.Payload.Timestamp);
            if (!anchor.Accepted)
            {
                return Results.BadRequest(new { status = "rejected", reason = anchor.Error });
            }

            return Results.Ok(new { status = "anchored", blockHash = anchor.BlockHash, channelId = anchor.ChannelId, cid = result.FileHash });
        });

        return app;
    }

    public static WebApplication MapIpfsIntegrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/integrations/ipfs/health", async (IConfiguration configuration) =>
        {
            string apiBase = (configuration["IpfsApiUrl"] ?? "http://127.0.0.1:5001/api/v0").Trim().TrimEnd('/');
            if (apiBase.EndsWith("/add", StringComparison.OrdinalIgnoreCase))
            {
                apiBase = apiBase[..^4];
            }

            string idUrl = $"{apiBase}/id";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var response = await client.PostAsync(idUrl, content: null);
                return Results.Ok(new
                {
                    integration = "ipfs",
                    apiUrl = apiBase,
                    reachable = response.IsSuccessStatusCode,
                    statusCode = (int)response.StatusCode
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    integration = "ipfs",
                    apiUrl = apiBase,
                    reachable = false,
                    error = ex.Message
                });
            }
        });

        return app;
    }

    private static GitCommitIntent? ParseGitIntent(string payload, HttpRequest request, string defaultProjectId)
    {
        try
        {
            var hookIntent = JsonSerializer.Deserialize<GitCommitIntent>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (hookIntent != null &&
                !string.IsNullOrWhiteSpace(hookIntent.Repository) &&
                !string.IsNullOrWhiteSpace(hookIntent.CommitHash))
            {
                return hookIntent with
                {
                    Provider = string.IsNullOrWhiteSpace(hookIntent.Provider)
                        ? "NexusGitHook"
                        : hookIntent.Provider
                };
            }
        }
        catch
        {
            // Ignore and fallback to GitHub push shape.
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string eventType = request.Headers.TryGetValue("X-GitHub-Event", out var eventHeader)
                ? eventHeader.ToString()
                : string.Empty;

            if (!string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string repository = "";
            if (root.TryGetProperty("repository", out var repoNode))
            {
                repository =
                    repoNode.TryGetProperty("full_name", out var fullName) ? fullName.GetString() ?? "" :
                    repoNode.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
            }

            if (!root.TryGetProperty("head_commit", out var headCommit) || headCommit.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            string commitHash = headCommit.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            string message = headCommit.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
            string author = "";
            if (headCommit.TryGetProperty("author", out var authorNode))
            {
                author =
                    authorNode.TryGetProperty("username", out var username) ? username.GetString() ?? "" :
                    authorNode.TryGetProperty("name", out var authorName) ? authorName.GetString() ?? "" : "";
            }

            if (string.IsNullOrWhiteSpace(author) && root.TryGetProperty("sender", out var sender))
            {
                author = sender.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "";
            }

            string gitRef = root.TryGetProperty("ref", out var refProp) ? refProp.GetString() ?? "" : "";
            string branch = gitRef.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
                ? gitRef["refs/heads/".Length..]
                : gitRef;

            string queryProjectId = request.Query.TryGetValue("projectId", out var projectValues) ? projectValues.ToString() : "";
            string projectId = !string.IsNullOrWhiteSpace(queryProjectId) ? queryProjectId : defaultProjectId;

            return new GitCommitIntent(
                Repository: repository,
                CommitHash: commitHash,
                Message: message,
                Author: author,
                PatchCid: "",
                ProjectId: projectId,
                Provider: "GitHubPush",
                Branch: branch);
        }
        catch
        {
            return null;
        }
    }
}

public record GitCommitIntent(
    string Repository,
    string CommitHash,
    string Message,
    string Author,
    string PatchCid,
    string ProjectId,
    string Provider = "NexusGitHook",
    string Branch = "");

public record ArtifactAnchorIntent(
    string ProjectId,
    string FileHash,
    string FileName,
    long SizeBytes,
    string ContentType,
    string User,
    string RegisteredBy,
    string VerificationMethod,
    string Timestamp,
    string UserPublicKey,
    string UserSignature);

public record GitRepositoryConnectRequest(
    string ProjectId,
    string Repository,
    string User,
    string Timestamp,
    string UserPublicKey,
    string UserSignature);
