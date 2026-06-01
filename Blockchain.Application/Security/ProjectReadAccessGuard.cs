using Blockchain.Core.Contracts;

namespace Blockchain.Application.Security;

public sealed class ProjectReadAccessGuard
{
    private readonly AuthorizeReadRequestUseCase _authorizeReadRequest;
    private readonly ISmartContractStateReader _stateReader;
    private readonly ProjectAccessPolicy _accessPolicy;

    public ProjectReadAccessGuard(
        AuthorizeReadRequestUseCase authorizeReadRequest,
        ISmartContractStateReader stateReader,
        ProjectAccessPolicy accessPolicy)
    {
        _authorizeReadRequest = authorizeReadRequest;
        _stateReader = stateReader;
        _accessPolicy = accessPolicy;
    }

    public ProjectReadAccessResult CanRead(ProjectReadAccessRequest request)
    {
        var authorization = _authorizeReadRequest.Execute(new AuthorizeReadRequestCommand(
            request.Scope,
            request.UserName,
            request.UserPublicKey,
            request.AuthSignature,
            request.AuthTimestamp,
            request.AuthNonce));

        if (!authorization.Authorized)
        {
            return new ProjectReadAccessResult(false, authorization.Message);
        }

        string role = _stateReader.GetUserRole(request.ProjectId, request.UserName);
        if (!_accessPolicy.CanReadProject(request.ProjectId, role))
        {
            return new ProjectReadAccessResult(false, "project access denied");
        }

        return new ProjectReadAccessResult(true, string.Empty);
    }
}

public sealed record ProjectReadAccessRequest(
    string ProjectId,
    string Scope,
    string UserName,
    string UserPublicKey,
    string AuthSignature,
    string AuthTimestamp,
    string AuthNonce);

public sealed record ProjectReadAccessResult(bool Allowed, string Reason);
