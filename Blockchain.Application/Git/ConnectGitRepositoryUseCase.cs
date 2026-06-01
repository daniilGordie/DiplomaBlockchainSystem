using System.Text.RegularExpressions;

namespace Blockchain.Application.Git;

public sealed class ConnectGitRepositoryUseCase
{
    private static readonly Regex ProjectIdPattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.\\-/:]+$", RegexOptions.Compiled);
    private static readonly TimeSpan SignatureTtl = TimeSpan.FromMinutes(10);

    private readonly IProjectMembershipReader _membershipReader;
    private readonly IGitRepositoryBindingStore _bindingStore;
    private readonly IRequestReplayGuard _replayGuard;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IClock _clock;

    public ConnectGitRepositoryUseCase(
        IProjectMembershipReader membershipReader,
        IGitRepositoryBindingStore bindingStore,
        IRequestReplayGuard replayGuard,
        ISignatureVerifier signatureVerifier,
        IClock clock)
    {
        _membershipReader = membershipReader;
        _bindingStore = bindingStore;
        _replayGuard = replayGuard;
        _signatureVerifier = signatureVerifier;
        _clock = clock;
    }

    public ConnectGitRepositoryResult Execute(ConnectGitRepositoryCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ProjectId) ||
            string.IsNullOrWhiteSpace(command.Repository) ||
            string.IsNullOrWhiteSpace(command.User) ||
            string.IsNullOrWhiteSpace(command.UserPublicKey) ||
            string.IsNullOrWhiteSpace(command.UserSignature))
        {
            return Invalid("Missing required fields");
        }

        string actor = command.User.Trim();
        string repository = NormalizeRepositoryName(command.Repository);
        if (!IsValidProjectId(command.ProjectId))
        {
            return Invalid("Invalid project id");
        }

        if (!IsValidRepository(repository))
        {
            return Invalid("Invalid repository");
        }

        if (!DateTime.TryParse(command.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var clientTimestamp))
        {
            return Invalid("Invalid timestamp");
        }

        DateTime nowUtc = _clock.UtcNow;
        if ((nowUtc - clientTimestamp.ToUniversalTime()).Duration() > SignatureTtl)
        {
            return Invalid("Expired request signature");
        }

        string signable = $"GIT_CONNECT:{command.ProjectId}:{repository}:{actor}:{command.Timestamp}";
        if (!_signatureVerifier.Verify(signable, command.UserSignature, command.UserPublicKey))
        {
            return new ConnectGitRepositoryResult(ConnectGitRepositoryStatus.Unauthorized, "Invalid signature");
        }

        string? boundPublicKey = _membershipReader.GetUserPublicKey(actor);
        if (string.IsNullOrWhiteSpace(boundPublicKey) ||
            !string.Equals(boundPublicKey, command.UserPublicKey, StringComparison.Ordinal))
        {
            return new ConnectGitRepositoryResult(ConnectGitRepositoryStatus.Unauthorized, "Public key is not bound to this user");
        }

        string role = _membershipReader.GetUserRole(command.ProjectId, actor);
        bool canConnect = string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase);
        if (!canConnect)
        {
            return new ConnectGitRepositoryResult(ConnectGitRepositoryStatus.Forbidden, "Only project Owner or Manager can connect repositories");
        }

        string replayKey = $"git-connect:{command.ProjectId}:{repository}:{actor}:{command.Timestamp}".ToLowerInvariant();
        if (!_replayGuard.TryRegister(replayKey, nowUtc))
        {
            return new ConnectGitRepositoryResult(ConnectGitRepositoryStatus.DuplicateRequest, "Duplicate connect request");
        }

        if (!_bindingStore.TryValidateOrBind(repository, command.ProjectId, out var bindingMessage))
        {
            return new ConnectGitRepositoryResult(ConnectGitRepositoryStatus.BindingConflict, bindingMessage);
        }

        return new ConnectGitRepositoryResult(
            ConnectGitRepositoryStatus.Connected,
            "Repository connected",
            repository,
            command.ProjectId);
    }

    public static string NormalizeRepositoryName(string repository)
    {
        string value = (repository ?? string.Empty).Trim();
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            string host = uri.Host.Trim().ToLowerInvariant();
            string path = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(path) ? host : $"{host}/{path}";
        }

        return value;
    }

    private static bool IsValidProjectId(string projectId)
    {
        return !string.IsNullOrWhiteSpace(projectId)
            && ProjectIdPattern.IsMatch(projectId);
    }

    private static bool IsValidRepository(string repository)
    {
        return !string.IsNullOrWhiteSpace(repository)
            && repository.Length <= 200
            && RepositoryPattern.IsMatch(repository);
    }

    private static ConnectGitRepositoryResult Invalid(string message)
    {
        return new ConnectGitRepositoryResult(ConnectGitRepositoryStatus.InvalidRequest, message);
    }
}
