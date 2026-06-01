using Blockchain.Core.Contracts;

namespace Blockchain.Application.Security;

public sealed class ChainReadAccessGuard
{
    private readonly AuthorizeReadRequestUseCase _authorizeReadRequest;
    private readonly ISmartContractStateReader _stateReader;
    private readonly ProjectAccessPolicy _accessPolicy;

    public ChainReadAccessGuard(
        AuthorizeReadRequestUseCase authorizeReadRequest,
        ISmartContractStateReader stateReader,
        ProjectAccessPolicy accessPolicy)
    {
        _authorizeReadRequest = authorizeReadRequest;
        _stateReader = stateReader;
        _accessPolicy = accessPolicy;
    }

    public ReadAccessResult CanRead(ChainReadAccessRequest request)
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
            return new ReadAccessResult(false, authorization.Message);
        }

        string role = _stateReader.GetUserRole(request.ChannelId, request.UserName);
        if (!_accessPolicy.CanReadProject(request.ChannelId, role))
        {
            return new ReadAccessResult(false, "chain access denied");
        }

        return new ReadAccessResult(true, string.Empty);
    }
}

public sealed class UserReadAccessGuard
{
    private readonly AuthorizeReadRequestUseCase _authorizeReadRequest;

    public UserReadAccessGuard(AuthorizeReadRequestUseCase authorizeReadRequest)
    {
        _authorizeReadRequest = authorizeReadRequest;
    }

    public ReadAccessResult CanRead(UserReadAccessRequest request)
    {
        var authorization = _authorizeReadRequest.Execute(new AuthorizeReadRequestCommand(
            request.Scope,
            request.UserName,
            request.UserPublicKey,
            request.AuthSignature,
            request.AuthTimestamp,
            request.AuthNonce));

        return new ReadAccessResult(authorization.Authorized, authorization.Message);
    }
}

public sealed record ChainReadAccessRequest(
    string ChannelId,
    string Scope,
    string UserName,
    string UserPublicKey,
    string AuthSignature,
    string AuthTimestamp,
    string AuthNonce);

public sealed record UserReadAccessRequest(
    string Scope,
    string UserName,
    string UserPublicKey,
    string AuthSignature,
    string AuthTimestamp,
    string AuthNonce);

public sealed record ReadAccessResult(bool Allowed, string Reason);
