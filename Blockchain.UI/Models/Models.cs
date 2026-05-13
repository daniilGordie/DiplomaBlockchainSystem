using System;

namespace Blockchain.UI.Models
{
    public enum ProjectTaskStatus { Todo = 0, InProgress = 1, Done = 2 }
    public enum ActiveTab { Board, Team, Governance, Documents, Artifacts, Analytics, Network }

    public class ProjectTask
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Creator { get; set; } = "";
        public string Assignee { get; set; } = "";
        public ProjectTaskStatus Status { get; set; }
        public string ProjectId { get; set; } = "";
        public string Description { get; set; } = "";
        public string ParentTaskId { get; set; } = "";
        public string BranchInfo { get; set; } = "";
    }

    public class TaskEvent
    {
        public string Type { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string Title { get; set; } = "";
        public int Status { get; set; }
        public string User { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Assignee { get; set; } = "";
        public string Description { get; set; } = "";
        public string ParentTaskId { get; set; } = "";
        public string BranchInfo { get; set; } = "";
        public string TargetUser { get; set; } = "";
        public string TargetPublicKey { get; set; } = "";
        public string Role { get; set; } = "";
        public int Amount { get; set; }
        public string ProposalId { get; set; } = "";
        public bool Vote { get; set; }
    }

    public class CommitPayloadUI
    {
        public string Repository { get; set; } = "";
        public string CommitHash { get; set; } = "";
        public string Author { get; set; } = "";
        public string Message { get; set; } = "";
        public string PatchCid { get; set; } = "";
    }

    public class ArtifactPayloadUI
    {
        public string FileName { get; set; } = "";
        public string FileHash { get; set; } = "";
        public string RegisteredBy { get; set; } = "";
        public string VerificationMethod { get; set; } = "Client-Side";
        public long SizeBytes { get; set; }
        public string ContentType { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    public class IntegrationStatusUI
    {
        public bool IsLoaded { get; set; }
        public bool IsHealthy { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "Not checked";
        public string Endpoint { get; set; } = "";
        public string Details { get; set; } = "";
    }

    public class AuditTrailEntry
    {
        public int BlockIndex { get; set; }
        public string BlockHash { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string EventType { get; set; } = "";
        public string Actor { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string Summary { get; set; } = "";
    }

    public class KeystoreModel
    {
        public int Version { get; set; } = 2;
        public string ProtectionMode { get; set; } = "password";
        public string Address { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Iv { get; set; } = "";
        public string Salt { get; set; } = "";
        public string PasskeyCredentialId { get; set; } = "";
        public string PasskeyWrappedKey { get; set; } = "";
    }

}
