using System.Collections.Concurrent;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public sealed class ProjectResponseCache
{
    private readonly IChainReader _chainReader;
    private readonly ConcurrentDictionary<string, AnalyticsResponse> _analytics = new();
    private readonly ConcurrentDictionary<string, SecurityAuditResponse> _securityAudits = new();

    public ProjectResponseCache(IChainReader chainReader)
    {
        _chainReader = chainReader;
    }

    public bool TryGetAnalytics(string projectId, out AnalyticsResponse response)
    {
        if (_analytics.TryGetValue(BuildProjectCacheKey(projectId), out var cached))
        {
            response = cached.Clone();
            return true;
        }

        response = new AnalyticsResponse();
        return false;
    }

    public void SetAnalytics(string projectId, AnalyticsResponse response)
    {
        _analytics[BuildProjectCacheKey(projectId)] = response.Clone();
    }

    public bool TryGetSecurityAudit(string projectId, out SecurityAuditResponse response)
    {
        if (_securityAudits.TryGetValue(BuildProjectCacheKey(projectId), out var cached))
        {
            response = cached.Clone();
            return true;
        }

        response = new SecurityAuditResponse();
        return false;
    }

    public void SetSecurityAudit(string projectId, SecurityAuditResponse response)
    {
        _securityAudits[BuildProjectCacheKey(projectId)] = response.Clone();
    }

    public void InvalidateProject(string projectId)
    {
        string safeProjectId = ChannelName.Normalize(projectId);
        string prefix = $"{safeProjectId}:";

        foreach (var key in _analytics.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _analytics.TryRemove(key, out _);
        }

        foreach (var key in _securityAudits.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _securityAudits.TryRemove(key, out _);
        }
    }

    private string BuildProjectCacheKey(string projectId)
    {
        string safeProjectId = ChannelName.Normalize(projectId);
        string latestHash = _chainReader.GetLatestBlock(safeProjectId)?.Hash ?? "empty";
        return $"{safeProjectId}:{latestHash}";
    }
}
