namespace Blockchain.Application.Projects;

public sealed class GetDocumentVersionsUseCase
{
    private readonly IDocumentReader _reader;

    public GetDocumentVersionsUseCase(IDocumentReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<DocumentVersionDto> Execute(string projectId, string documentId)
    {
        return _reader.GetDocumentVersions(projectId, documentId);
    }
}
