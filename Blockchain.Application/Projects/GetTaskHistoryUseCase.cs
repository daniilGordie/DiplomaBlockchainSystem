namespace Blockchain.Application.Projects;

public sealed class GetTaskHistoryUseCase
{
    private readonly IProjectTaskReader _reader;

    public GetTaskHistoryUseCase(IProjectTaskReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<TaskHistoryDto> Execute(string projectId, string taskId)
    {
        return _reader.GetTaskHistory(projectId, taskId);
    }
}
