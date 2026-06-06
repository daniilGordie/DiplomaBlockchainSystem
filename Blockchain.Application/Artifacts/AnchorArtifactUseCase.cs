using System.Text.RegularExpressions;
using Blockchain.Application.Git;

namespace Blockchain.Application.Artifacts;

public sealed class AnchorArtifactUseCase
{
    private static readonly Regex ProjectIdPattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private static readonly TimeSpan SignatureTtl = TimeSpan.FromMinutes(10);
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;

    private readonly IProjectMembershipReader _membershipReader;
    private readonly IRequestReplayGuard _replayGuard;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IClock _clock;

    public AnchorArtifactUseCase(
        IProjectMembershipReader membershipReader,
        IRequestReplayGuard replayGuard,
        ISignatureVerifier signatureVerifier,
        IClock clock)
    {
        _membershipReader = membershipReader;
        _replayGuard = replayGuard;
        _signatureVerifier = signatureVerifier;
        _clock = clock;
    }

    public AnchorArtifactResult Execute(AnchorArtifactCommand command)
    {
        string actor = string.IsNullOrWhiteSpace(command.RegisteredBy) ? command.User.Trim() : command.RegisteredBy.Trim();
        if (string.IsNullOrWhiteSpace(actor) ||
            string.IsNullOrWhiteSpace(command.ProjectId) ||
            string.IsNullOrWhiteSpace(command.FileHash) ||
            string.IsNullOrWhiteSpace(command.FileName) ||
            string.IsNullOrWhiteSpace(command.UserPublicKey) ||
            string.IsNullOrWhiteSpace(command.UserSignature))
        {
            return Invalid("Missing required fields");
        }

        if (!IsValidProjectId(command.ProjectId))
        {
            return Invalid("Invalid project id");
        }

        if (!IsLikelyIpfsCid(command.FileHash))
        {
            return Invalid("Invalid IPFS CID format");
        }

        if (command.FileName.Length > 256)
        {
            return Invalid("Invalid file name");
        }

        if (command.SizeBytes <= 0 || command.SizeBytes > MaxFileSizeBytes)
        {
            return Invalid("Invalid file size");
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

        string signable = $"ARTIFACT_REGISTER:{command.ProjectId}:{command.FileHash}:{actor}:{command.Timestamp}";
        if (!_signatureVerifier.Verify(signable, command.UserSignature, command.UserPublicKey))
        {
            return new AnchorArtifactResult(AnchorArtifactStatus.Unauthorized, "Invalid signature");
        }

        string? boundPublicKey = _membershipReader.GetUserPublicKey(actor);
        if (string.IsNullOrWhiteSpace(boundPublicKey) ||
            !string.Equals(boundPublicKey, command.UserPublicKey, StringComparison.Ordinal))
        {
            return new AnchorArtifactResult(AnchorArtifactStatus.Unauthorized, "Public key is not bound to this user");
        }

        string role = _membershipReader.GetUserRole(command.ProjectId, actor);
        if (role == "None")
        {
            return new AnchorArtifactResult(AnchorArtifactStatus.Forbidden, "User has no project role");
        }

        string replayKey = $"{command.ProjectId}:{command.FileHash}:{actor}".ToLowerInvariant();
        if (!_replayGuard.TryRegister(replayKey, nowUtc))
        {
            return new AnchorArtifactResult(AnchorArtifactStatus.DuplicateIgnored, "duplicate_ignored", command.ProjectId, command.FileHash);
        }

        string timestamp = nowUtc.ToString("O");
        var payload = new AnchorArtifactPayload(
            Source: "ArtifactRegistry",
            Type: "Register",
            FileName: command.FileName,
            FileHash: command.FileHash,
            SizeBytes: command.SizeBytes,
            ContentType: string.IsNullOrWhiteSpace(command.ContentType) ? "application/octet-stream" : command.ContentType,
            User: actor,
            ProjectId: command.ProjectId,
            RegisteredBy: actor,
            VerificationMethod: string.IsNullOrWhiteSpace(command.VerificationMethod) ? "IPFS CID" : command.VerificationMethod,
            Timestamp: timestamp);

        return new AnchorArtifactResult(AnchorArtifactStatus.ReadyToAnchor, "ready", command.ProjectId, command.FileHash, payload);
    }

    private static bool IsValidProjectId(string projectId)
    {
        return !string.IsNullOrWhiteSpace(projectId)
            && ProjectIdPattern.IsMatch(projectId);
    }

    private static bool IsLikelyIpfsCid(string cid)
    {
        if (string.IsNullOrWhiteSpace(cid) || cid.Length < 40 || cid.Length > 128)
        {
            return false;
        }

        foreach (char c in cid)
        {
            bool ok = (c >= 'a' && c <= 'z') ||
                      (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static AnchorArtifactResult Invalid(string message)
    {
        return new AnchorArtifactResult(AnchorArtifactStatus.InvalidRequest, message);
    }
}
