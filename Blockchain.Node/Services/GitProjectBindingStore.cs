using System.Text.Json;
using Blockchain.Application.Git;

namespace Blockchain.Node.Services
{
    public sealed class GitProjectBindingStore : IGitRepositoryBindingStore
    {
        private readonly string _filePath;
        private readonly object _sync = new();
        private Dictionary<string, string> _repoToProject;

        public GitProjectBindingStore(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppContext.BaseDirectory, "git_repo_bindings.json")
                : filePath;
            _repoToProject = LoadFromDisk();
        }

        public bool TryValidateOrBind(string repository, string projectId, out string message)
        {
            message = string.Empty;
            string repoKey = Normalize(repository);
            string projectKey = Normalize(projectId);

            if (string.IsNullOrWhiteSpace(repoKey) || string.IsNullOrWhiteSpace(projectKey))
            {
                message = "Repository and project id are required for binding.";
                return false;
            }

            lock (_sync)
            {
                if (_repoToProject.TryGetValue(repoKey, out var boundProject))
                {
                    if (!string.Equals(boundProject, projectKey, StringComparison.Ordinal))
                    {
                        message = $"Repository '{repository}' is already bound to project '{boundProject}'.";
                        return false;
                    }

                    return true;
                }

                foreach (var pair in _repoToProject)
                {
                    if (string.Equals(pair.Value, projectKey, StringComparison.Ordinal) &&
                        !string.Equals(pair.Key, repoKey, StringComparison.Ordinal))
                    {
                        message = $"Project '{projectId}' is already bound to repository '{pair.Key}'.";
                        return false;
                    }
                }

                _repoToProject[repoKey] = projectKey;
                SaveToDisk(_repoToProject);
                return true;
            }
        }

        public IReadOnlyDictionary<string, string> GetBindingsSnapshot()
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_repoToProject, StringComparer.Ordinal);
            }
        }

        private Dictionary<string, string> LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                string json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return data != null
                    ? new Dictionary<string, string>(data, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private void SaveToDisk(Dictionary<string, string> bindings)
        {
            try
            {
                string json = JsonSerializer.Serialize(bindings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Non-fatal: keep runtime bindings in memory.
            }
        }

        private static string Normalize(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
