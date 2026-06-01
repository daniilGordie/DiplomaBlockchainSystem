using Blockchain.Application.Projects;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public static class GrpcProjectMapper
{
    public static BlockModel ToBlockModel(Block block)
    {
        return new BlockModel
        {
            Index = block.Index,
            Timestamp = block.Timestamp.ToString("O"),
            Data = block.Data,
            PreviousHash = block.PreviousHash,
            Hash = block.Hash,
            ValidatorPublicKey = block.ValidatorPublicKey ?? string.Empty,
            Signature = block.Signature ?? string.Empty,
            Nonce = block.Nonce,
            ChannelId = block.ChannelId
        };
    }

    public static TaskItem ToTaskItem(ProjectTaskDto task)
    {
        return new TaskItem
        {
            Id = task.Id,
            Title = task.Title,
            Creator = task.Creator,
            Assignee = task.Assignee,
            Status = task.Status,
            ProjectId = task.ProjectId,
            Description = task.Description,
            ParentTaskId = task.ParentTaskId,
            BranchInfo = task.BranchInfo
        };
    }

    public static TaskHistoryItem ToTaskHistoryItem(TaskHistoryDto item)
    {
        return new TaskHistoryItem
        {
            Timestamp = item.Timestamp,
            User = item.User,
            Action = item.Action,
            StatusLabel = item.StatusLabel,
            BlockIndex = item.BlockIndex
        };
    }

    public static GovernanceProposalItem ToGovernanceProposalItem(GovernanceProposalDto proposal)
    {
        return new GovernanceProposalItem
        {
            ProposalId = proposal.ProposalId,
            ProjectId = proposal.ProjectId,
            Title = proposal.Title,
            Description = proposal.Description,
            CreatedBy = proposal.CreatedBy,
            CreatedAt = proposal.CreatedAt,
            Status = proposal.Status,
            YesVotes = proposal.YesVotes,
            NoVotes = proposal.NoVotes,
            UserVote = proposal.UserVote
        };
    }

    public static DocumentSummary ToDocumentSummary(DocumentSummaryDto document)
    {
        return new DocumentSummary
        {
            DocumentId = document.DocumentId,
            Title = document.Title,
            LatestVersion = document.LatestVersion,
            UpdatedBy = document.UpdatedBy,
            UpdatedAt = document.UpdatedAt,
            ContentHash = document.ContentHash,
            Content = document.Content
        };
    }

    public static DocumentVersionItem ToDocumentVersionItem(DocumentVersionDto version)
    {
        return new DocumentVersionItem
        {
            DocumentId = version.DocumentId,
            Version = version.Version,
            Title = version.Title,
            Content = version.Content,
            ContentHash = version.ContentHash,
            UpdatedBy = version.UpdatedBy,
            UpdatedAt = version.UpdatedAt
        };
    }
}
