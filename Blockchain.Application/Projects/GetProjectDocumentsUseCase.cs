namespace Blockchain.Application.Projects;

public sealed class GetProjectDocumentsUseCase
{
    private readonly IDocumentReader _reader;

    public GetProjectDocumentsUseCase(IDocumentReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<DocumentSummaryDto> Execute(string projectId)
    {
        return _reader.GetDocuments(projectId);
    }
}
