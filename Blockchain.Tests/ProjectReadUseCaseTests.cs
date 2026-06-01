using Blockchain.Application.Projects;

namespace Blockchain.Tests;

public class ProjectReadUseCaseTests
{
    [Fact]
    public void GetProjectTasks_ShouldReturnReaderResult()
    {
        var reader = new FakeProjectTaskReader();
        var useCase = new GetProjectTasksUseCase(reader);

        var result = useCase.Execute("ProjectA", "Alice");

        Assert.Equal("Developer", result.UserRole);
        Assert.Single(result.Tasks);
        Assert.Equal("TSK-1", result.Tasks[0].Id);
    }

    [Fact]
    public void GetGovernanceProposals_ShouldReturnReaderResult()
    {
        var useCase = new GetGovernanceProposalsUseCase(new FakeGovernanceReader());

        var result = useCase.Execute("ProjectA", "Alice");

        Assert.Single(result);
        Assert.Equal("GOV-1", result[0].ProposalId);
        Assert.Equal("Yes", result[0].UserVote);
    }

    [Fact]
    public void GetDocumentVersions_ShouldReturnReaderResult()
    {
        var useCase = new GetDocumentVersionsUseCase(new FakeDocumentReader());

        var result = useCase.Execute("ProjectA", "DOC-1");

        Assert.Single(result);
        Assert.Equal(2, result[0].Version);
        Assert.Equal("Alice", result[0].UpdatedBy);
    }

    private sealed class FakeProjectTaskReader : IProjectTaskReader
    {
        public ProjectTaskListResult GetProjectTasks(string projectId, string userName)
        {
            return new ProjectTaskListResult(
                "Developer",
                new[]
                {
                    new ProjectTaskDto("TSK-1", "Task", "Alice", "Bob", 1, projectId, "Desc", "", "main")
                });
        }

        public IReadOnlyList<TaskHistoryDto> GetTaskHistory(string projectId, string taskId)
        {
            return new[]
            {
                new TaskHistoryDto("today", "Alice", "Create", "To Do", 1)
            };
        }
    }

    private sealed class FakeGovernanceReader : IGovernanceReader
    {
        public IReadOnlyList<GovernanceProposalDto> GetProposals(string projectId, string userName)
        {
            return new[]
            {
                new GovernanceProposalDto("GOV-1", projectId, "Title", "Desc", "Alice", "today", "Approved", 2, 0, "Yes")
            };
        }
    }

    private sealed class FakeDocumentReader : IDocumentReader
    {
        public IReadOnlyList<DocumentSummaryDto> GetDocuments(string projectId)
        {
            return new[]
            {
                new DocumentSummaryDto("DOC-1", "Doc", 2, "Alice", "today", "hash", "content")
            };
        }

        public IReadOnlyList<DocumentVersionDto> GetDocumentVersions(string projectId, string documentId)
        {
            return new[]
            {
                new DocumentVersionDto(documentId, 2, "Doc", "content", "hash", "Alice", "today")
            };
        }
    }
}
