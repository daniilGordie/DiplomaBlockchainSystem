namespace Blockchain.Application.Projects;

public sealed class GetProjectTasksUseCase
{
    private readonly IProjectTaskReader _reader;

    public GetProjectTasksUseCase(IProjectTaskReader reader)
    {
        _reader = reader;
    }

    public ProjectTaskListResult Execute(string projectId, string userName)
    {
        return _reader.GetProjectTasks(projectId, userName);
    }
}
