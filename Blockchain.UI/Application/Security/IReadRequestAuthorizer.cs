using Blockchain.Node;

namespace Blockchain.UI.Application.Security;

public interface IReadRequestAuthorizer
{
    void Apply(ChainRequest request, string scope);
    void Apply(ProjectRequest request, string scope);
    void Apply(TaskHistoryRequest request, string scope);
    void Apply(DocumentHistoryRequest request, string scope);
    void Apply(UserRequest request, string scope);
}
