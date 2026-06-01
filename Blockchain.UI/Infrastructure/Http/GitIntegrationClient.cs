using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.UI.Application.Clients;
using Blockchain.UI.Models;

namespace Blockchain.UI.Infrastructure.Http;

public sealed class GitIntegrationClient : IGitIntegrationClient
{
    private readonly HttpClient _http;

    public GitIntegrationClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IntegrationStatusUI> GetStatusAsync(string nodeUrl)
    {
        try
        {
            using var doc = await _http.GetFromJsonAsync<JsonDocument>($"{nodeUrl}/api/integrations/git/status");
            var root = doc?.RootElement;
            string webhookUrl = root?.TryGetProperty("webhookUrl", out var webhookProp) == true ? webhookProp.GetString() ?? "" : "";
            string mode = root?.TryGetProperty("mode", out var modeProp) == true ? modeProp.GetString() ?? "" : "";
            string source = root?.TryGetProperty("source", out var sourceProp) == true ? sourceProp.GetString() ?? "" : "";
            string defaultProjectId = root?.TryGetProperty("defaultProjectId", out var defaultProjectProp) == true ? defaultProjectProp.GetString() ?? "" : "";
            string bindingPolicy = root?.TryGetProperty("bindingPolicy", out var bindingPolicyProp) == true ? bindingPolicyProp.GetString() ?? "" : "";
            int bindingsCount = root?.TryGetProperty("bindingsCount", out var bindingsCountProp) == true ? bindingsCountProp.GetInt32() : 0;
            bool secretConfigured = root?.TryGetProperty("webhookSecretConfigured", out var secretProp) == true && secretProp.GetBoolean();

            var providers = ReadStringArray(root, "providers");
            var bindings = ReadBindings(root);
            string details = BuildDetails(mode, source, providers, bindingPolicy, defaultProjectId, bindingsCount);

            return new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = secretConfigured,
                Name = "Git webhook",
                Status = secretConfigured ? "Ready" : "Secret missing",
                Endpoint = webhookUrl,
                Details = details,
                Source = source,
                Providers = providers,
                RepositoryBindings = bindings
            };
        }
        catch (Exception ex)
        {
            return new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = false,
                Name = "Git webhook",
                Status = "Unavailable",
                Endpoint = $"{nodeUrl}/api/webhooks/git",
                Details = ex.Message
            };
        }
    }

    public async Task<GitRepositoryConnectResult> ConnectRepositoryAsync(GitRepositoryConnectCommand command)
    {
        var request = new GitRepositoryConnectRequest
        {
            ProjectId = command.ProjectId,
            Repository = command.Repository,
            User = command.User,
            Timestamp = command.Timestamp,
            UserPublicKey = command.UserPublicKey,
            UserSignature = command.UserSignature
        };

        using var response = await _http.PostAsJsonAsync($"{command.NodeUrl}/api/integrations/git/connect", request);
        if (response.IsSuccessStatusCode)
        {
            return new GitRepositoryConnectResult(true, command.Repository, $"Repository connected: {command.Repository}.");
        }

        string details = await response.Content.ReadAsStringAsync();
        return new GitRepositoryConnectResult(
            false,
            command.Repository,
            $"Repository connect failed ({(int)response.StatusCode}): {details}");
    }

    private static List<string> ReadStringArray(JsonElement? root, string propertyName)
    {
        if (root?.TryGetProperty(propertyName, out var prop) != true || prop.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return prop.EnumerateArray()
            .Select(x => x.GetString() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static List<GitRepositoryBindingUI> ReadBindings(JsonElement? root)
    {
        if (root?.TryGetProperty("bindings", out var bindingsProp) != true || bindingsProp.ValueKind != JsonValueKind.Array)
        {
            return new List<GitRepositoryBindingUI>();
        }

        return bindingsProp.EnumerateArray()
            .Select(x => new GitRepositoryBindingUI
            {
                Repository = x.TryGetProperty("repository", out var repoProp) ? repoProp.GetString() ?? "" : "",
                ProjectId = x.TryGetProperty("projectId", out var projectProp) ? projectProp.GetString() ?? "" : ""
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Repository) || !string.IsNullOrWhiteSpace(x.ProjectId))
            .ToList();
    }

    private static string BuildDetails(
        string mode,
        string source,
        List<string> providers,
        string bindingPolicy,
        string defaultProjectId,
        int bindingsCount)
    {
        string details = mode;
        if (!string.IsNullOrWhiteSpace(source))
        {
            details += $" | source: {source}";
        }
        if (providers.Any())
        {
            details += $" | providers: {string.Join(", ", providers)}";
        }
        if (!string.IsNullOrWhiteSpace(bindingPolicy))
        {
            details += $" | {bindingPolicy}";
        }
        if (!string.IsNullOrWhiteSpace(defaultProjectId))
        {
            details += $" | default project: {defaultProjectId}";
        }

        return $"{details} | bindings: {bindingsCount}";
    }

    private sealed class GitRepositoryConnectRequest
    {
        public string ProjectId { get; set; } = "";
        public string Repository { get; set; } = "";
        public string User { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string UserPublicKey { get; set; } = "";
        public string UserSignature { get; set; } = "";
    }
}
