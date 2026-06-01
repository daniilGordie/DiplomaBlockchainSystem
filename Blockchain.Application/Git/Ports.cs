namespace Blockchain.Application.Git;

public interface IGitRepositoryBindingStore
{
    bool TryValidateOrBind(string repository, string projectId, out string message);
}

public interface IProjectMembershipReader
{
    string? GetUserPublicKey(string userName);
    string GetUserRole(string projectId, string userName);
}

public interface IRequestReplayGuard
{
    bool TryRegister(string key, DateTime nowUtc);
}

public interface ISignatureVerifier
{
    bool Verify(string data, string signatureBase64, string publicKeyBase64);
}

public interface IClock
{
    DateTime UtcNow { get; }
}
