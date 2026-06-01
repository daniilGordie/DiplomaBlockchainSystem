using Blockchain.Node;
using Blockchain.UI.Services;

namespace Blockchain.UI.Application.Security;

public sealed class ReadRequestAuthorizer : IReadRequestAuthorizer
{
    private readonly KeyService _keyService;

    public ReadRequestAuthorizer(KeyService keyService)
    {
        _keyService = keyService;
    }

    public void Apply(ChainRequest request, string scope)
    {
        var auth = Build(scope);
        request.AuthSignature = auth.Signature;
        request.AuthTimestamp = auth.Timestamp;
        request.AuthNonce = auth.Nonce;
    }

    public void Apply(ProjectRequest request, string scope)
    {
        var auth = Build(scope);
        request.AuthSignature = auth.Signature;
        request.AuthTimestamp = auth.Timestamp;
        request.AuthNonce = auth.Nonce;
    }

    public void Apply(TaskHistoryRequest request, string scope)
    {
        var auth = Build(scope);
        request.AuthSignature = auth.Signature;
        request.AuthTimestamp = auth.Timestamp;
        request.AuthNonce = auth.Nonce;
    }

    public void Apply(DocumentHistoryRequest request, string scope)
    {
        var auth = Build(scope);
        request.AuthSignature = auth.Signature;
        request.AuthTimestamp = auth.Timestamp;
        request.AuthNonce = auth.Nonce;
    }

    public void Apply(UserRequest request, string scope)
    {
        var auth = Build(scope);
        request.AuthSignature = auth.Signature;
        request.AuthTimestamp = auth.Timestamp;
        request.AuthNonce = auth.Nonce;
    }

    private ReadAuthToken Build(string scope)
    {
        if (!_keyService.IsLoggedIn || string.IsNullOrWhiteSpace(_keyService.PublicKey))
        {
            return new ReadAuthToken("", "", "");
        }

        string userName = _keyService.UserName ?? "Guest";
        string publicKey = _keyService.PublicKey ?? string.Empty;
        string timestamp = DateTime.UtcNow.ToString("O");
        string nonce = Guid.NewGuid().ToString("N");
        string signable = $"READ:{scope}:{userName}:{publicKey}:{timestamp}:{nonce}";
        return new ReadAuthToken(_keyService.SignData(signable), timestamp, nonce);
    }

    private sealed record ReadAuthToken(string Signature, string Timestamp, string Nonce);
}
