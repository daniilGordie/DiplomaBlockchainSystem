namespace Blockchain.UI.Application.Clients;

public interface IBlockAnchoringService
{
    Task<UserNameAvailabilityResult> CheckUserNameAvailabilityAsync(string userName);
    Task<UserIdentityCheckResult> CheckCurrentUserIdentityAsync();
    Task<BlockAnchorResult> AnchorJsonStringAsync(string json, string targetChannel);
    Task<BlockAnchorResult> AnchorJsonStringWithKeystoreAsync(
        string json,
        string targetChannel,
        string keystore,
        string password,
        string? signerPublicKey = null);
}

public sealed record BlockAnchorResult(
    bool Success,
    string Message);

public sealed record UserIdentityCheckResult(
    bool Success,
    bool Exists,
    bool PublicKeyMatches,
    string Message);

public sealed record UserNameAvailabilityResult(
    bool Success,
    bool Exists,
    string Message);
