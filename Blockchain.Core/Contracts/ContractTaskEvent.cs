namespace Blockchain.Core.Contracts
{
    public class ContractTaskEvent
    {
        public string Type { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string Title { get; set; } = "";
        public int Status { get; set; }
        public string User { get; set; } = "";
        public string ProjectId { get; set; } = "";

        public string TargetUser { get; set; } = "";
        public string TargetPublicKey { get; set; } = "";
        public string Role { get; set; } = "";
        public int Amount { get; set; }

        public string Description { get; set; } = "";
        public string ParentTaskId { get; set; } = "";
        public string BranchInfo { get; set; } = "";
        public string Assignee { get; set; } = "";
        public string ProposalId { get; set; } = "";
        public bool Vote { get; set; }
    }
}
