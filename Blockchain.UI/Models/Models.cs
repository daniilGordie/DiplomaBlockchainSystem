using System;

namespace Blockchain.UI.Models
{
    public enum ProjectTaskStatus { Todo = 0, InProgress = 1, Done = 2 }
    public enum ActiveTab { Board, Team, Artifacts, Analytics }

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
        public string Role { get; set; } = "";
        public int Amount { get; set; }
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
    }

    public class KeystoreModel
    {
        public string Address { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Iv { get; set; } = "";
        public string Salt { get; set; } = "";
    }

    public class GitCommitIntent
    {
        public string Repository { get; set; } = "";
        public string CommitHash { get; set; } = "";
        public string Message { get; set; } = "";
        public string Author { get; set; } = "";
        public string PatchCid { get; set; } = "";
    }
}