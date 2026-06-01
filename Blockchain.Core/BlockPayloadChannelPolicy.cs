using System.Text.Json;

namespace Blockchain.Core;

public static class BlockPayloadChannelPolicy
{
    public static bool IsConsistent(string data, string channelId)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return true;
        }

        string trimmed = data.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var root = doc.RootElement;
            string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
            string projectId = root.TryGetProperty("ProjectId", out var projectProp) ? projectProp.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(type))
            {
                return true;
            }

            string safeChannel = ChannelName.Normalize(channelId);
            string safeProject = ChannelName.Normalize(projectId);

            bool isSystemOnlyEvent = type == "CreateProject" || type == "AssignRole";
            if (isSystemOnlyEvent)
            {
                return safeChannel == "System";
            }

            bool isProjectScopedEvent =
                type == "Create" || type == "Update" || type == "Move" ||
                type == "CreateDocument" || type == "UpdateDocument" ||
                type == "CreateProposal" || type == "CastVote" ||
                type == "CodeCommit" || type == "Register" || type == "Transfer";

            if (!isProjectScopedEvent)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(projectId))
            {
                return false;
            }

            if (safeChannel == "System")
            {
                return false;
            }

            return safeProject == safeChannel;
        }
        catch
        {
            return false;
        }
    }
}
