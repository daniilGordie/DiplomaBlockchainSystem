using Microsoft.JSInterop;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.X509;
using Blockchain.UI.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using System;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Signers;

namespace Blockchain.UI.Services
{
    public class KeyService
    {
        private string _privateKey = "";
        private string _sessionKeystore = "";
        private readonly IJSRuntime _jsRuntime;
        private readonly MnemonicService _mnemonicService;

        public string? PublicKey { get; private set; }
        public string UserName { get; private set; } = "Guest";
        public string? CurrentMnemonic { get; private set; }

        public bool IsLoggedIn => !string.IsNullOrEmpty(_privateKey);
        public bool IsLocked => !IsLoggedIn && !string.IsNullOrWhiteSpace(_sessionKeystore) && !string.IsNullOrWhiteSpace(PublicKey) && UserName != "Guest";

        public KeyService(IJSRuntime jsRuntime, MnemonicService mnemonicService)
        {
            _jsRuntime = jsRuntime;
            _mnemonicService = mnemonicService;
        }

        public async Task<string> CreateNewWallet(string userName)
        {
            string mnemonic = _mnemonicService.GenerateMnemonic();
            CurrentMnemonic = mnemonic;
            UserName = userName;
            await DeriveAndSaveKeys(mnemonic, userName);
            return mnemonic;
        }

        public async Task<bool> RestoreWallet(string mnemonic, string userName)
        {
            if (!_mnemonicService.ValidateMnemonic(mnemonic)) return false;

            UserName = userName;
            CurrentMnemonic = mnemonic;
            await DeriveAndSaveKeys(mnemonic, userName);
            return true;
        }

        private async Task DeriveAndSaveKeys(string mnemonic, string userName)
        {
            byte[] seed = _mnemonicService.GenerateSeed(mnemonic);
            byte[] privateKeyBytes = new byte[32];
            Array.Copy(seed, privateKeyBytes, 32);

            var curve = SecNamedCurves.GetByName("secp256r1");
            var domainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);

            var privKeyParams = new ECPrivateKeyParameters(new BigInteger(1, privateKeyBytes), domainParams);
            var q = curve.G.Multiply(privKeyParams.D);
            var pubKeyParams = new ECPublicKeyParameters(q, domainParams);

            var subjectPublicKeyInfo = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pubKeyParams);
            byte[] pubKeyBytes = subjectPublicKeyInfo.GetEncoded();

            PublicKey = Convert.ToBase64String(pubKeyBytes);
            _privateKey = Convert.ToBase64String(privateKeyBytes);

            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_user", userName);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_pub", PublicKey);
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_priv");
        }

        public async Task<bool> LoadFromStorage()
        {
            try
            {
                var storedUser = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_user");
                var storedPub = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_pub");
                var storedSession = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_session_wallet");
                await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_priv");

                if (!string.IsNullOrEmpty(storedUser) && !string.IsNullOrEmpty(storedPub) && !string.IsNullOrEmpty(storedSession))
                {
                    UserName = storedUser;
                    PublicKey = storedPub;
                    _sessionKeystore = storedSession;
                    _privateKey = "";

                    return false;
                }
            }
            catch { }
            return false;
        }

        public async Task SaveSessionWithPasswordAsync(string password)
        {
            string keystore = ExportKeystore(password);
            _sessionKeystore = keystore;
            await PersistSessionMetadataAsync(keystore);
        }

        public async Task SaveSessionWithPasskeyAsync()
        {
            string keystore = await ExportKeystoreWithPasskeyAsync();
            _sessionKeystore = keystore;
            await PersistSessionMetadataAsync(keystore);
        }

        public async Task StoreSessionKeystoreAsync(string keystore)
        {
            if (string.IsNullOrWhiteSpace(keystore)) return;
            _sessionKeystore = keystore;
            await PersistSessionMetadataAsync(keystore);
        }

        public async Task<bool> UnlockSessionWithPasswordAsync(string password)
        {
            if (string.IsNullOrWhiteSpace(_sessionKeystore)) return false;
            _privateKey = DecryptPrivateKeyFromPasswordKeystore(_sessionKeystore, password);
            await PersistSessionMetadataAsync(_sessionKeystore);
            return true;
        }

        public async Task<bool> UnlockSessionWithPasskeyAsync()
        {
            if (string.IsNullOrWhiteSpace(_sessionKeystore)) return false;
            _privateKey = await DecryptPrivateKeyFromPasskeyKeystoreAsync(_sessionKeystore);
            await PersistSessionMetadataAsync(_sessionKeystore);
            return true;
        }

        private async Task PersistSessionMetadataAsync(string keystore)
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_user", UserName);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_pub", PublicKey ?? "");
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_session_wallet", keystore);
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_priv");
        }

        public string SignData(string data)
        {
            if (string.IsNullOrEmpty(_privateKey))
                throw new InvalidOperationException("Private key is not loaded. Sign in again.");

            byte[] privateKeyBytes = Convert.FromBase64String(_privateKey);
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);

            var curve = SecNamedCurves.GetByName("secp256r1");
            var domainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
            var privKeyParams = new ECPrivateKeyParameters(new BigInteger(1, privateKeyBytes), domainParams);

            var digest = new Sha256Digest();
            digest.BlockUpdate(dataBytes, 0, dataBytes.Length);
            byte[] hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);

            var signer = new ECDsaSigner();
            signer.Init(true, privKeyParams);
            var signature = signer.GenerateSignature(hash);

            var r = signature[0].ToByteArrayUnsigned();
            var s = signature[1].ToByteArrayUnsigned();

            byte[] p1363Signature = new byte[64];

            Buffer.BlockCopy(r, 0, p1363Signature, 32 - r.Length, r.Length);
            Buffer.BlockCopy(s, 0, p1363Signature, 64 - s.Length, s.Length);

            return Convert.ToBase64String(p1363Signature);
        }

        public string ExportKeystore(string password)
        {
            if (string.IsNullOrEmpty(_privateKey)) throw new Exception("No key for export.");

            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);

            byte[] iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);

            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
            byte[] aesKey = pbkdf2.GetBytes(32);

            return ExportKeystoreWithRawAesKey(aesKey, salt, iv, "password", "", "");
        }

        public async Task<string> ExportKeystoreWithPasskeyAsync()
        {
            if (string.IsNullOrEmpty(_privateKey)) throw new Exception("No key for export.");

            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);

            byte[] iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);

            byte[] aesKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(aesKey);

            string wrapJson = await _jsRuntime.InvokeAsync<string>("nexusPasskey.registerAndWrapKeyJson", UserName, Convert.ToBase64String(aesKey));
            var (credentialId, wrappedKey) = ReadPasskeyWrapResult(wrapJson);
            if (string.IsNullOrWhiteSpace(credentialId) || string.IsNullOrWhiteSpace(wrappedKey))
            {
                throw new Exception("Passkey enrollment failed.");
            }

            return ExportKeystoreWithRawAesKey(aesKey, salt, iv, "passkey", credentialId, wrappedKey);
        }

        private string ExportKeystoreWithRawAesKey(byte[] aesKey, byte[] salt, byte[] iv, string protectionMode, string passkeyCredentialId, string passkeyWrappedKey)
        {
            if (string.IsNullOrEmpty(_privateKey)) throw new Exception("No key for export.");

            byte[] inputBytes = Encoding.UTF8.GetBytes(_privateKey);

            var engine = new AesEngine();
            var blockCipher = new CbcBlockCipher(engine);
            var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

            var keyParam = new KeyParameter(aesKey);
            var parameters = new ParametersWithIV(keyParam, iv);

            cipher.Init(true, parameters);

            byte[] ciphertext = new byte[cipher.GetOutputSize(inputBytes.Length)];
            int len = cipher.ProcessBytes(inputBytes, 0, inputBytes.Length, ciphertext, 0);
            cipher.DoFinal(ciphertext, len);

            return new JsonObject
            {
                ["Version"] = 2,
                ["ProtectionMode"] = protectionMode,
                ["Address"] = PublicKey ?? "",
                ["Ciphertext"] = Convert.ToBase64String(ciphertext),
                ["Iv"] = Convert.ToBase64String(iv),
                ["Salt"] = Convert.ToBase64String(salt),
                ["PasskeyCredentialId"] = passkeyCredentialId,
                ["PasskeyWrappedKey"] = passkeyWrappedKey
            }.ToJsonString();
        }

        public string SignDataWithKeystore(string keystoreJson, string password, string dataToSign)
        {
            string decryptedPrivateKey = DecryptPrivateKeyFromPasswordKeystore(keystoreJson, password);
            string tempBackup = _privateKey;
            _privateKey = decryptedPrivateKey;
            string signature = SignData(dataToSign);
            _privateKey = tempBackup;
            return signature;
        }

        public string GetPublicKeyFromKeystore(string keystoreJson)
        {
            var keystore = ReadKeystore(keystoreJson);
            return keystore.Address ?? string.Empty;
        }

        public async Task<string> SignDataWithPasskeyKeystoreAsync(string keystoreJson, string dataToSign)
        {
            string decryptedPrivateKey = await DecryptPrivateKeyFromPasskeyKeystoreAsync(keystoreJson);
            string tempBackup = _privateKey;
            _privateKey = decryptedPrivateKey;
            string signature = SignData(dataToSign);
            _privateKey = tempBackup;
            return signature;
        }

        private string DecryptPrivateKeyFromPasswordKeystore(string keystoreJson, string password)
        {
            var keystore = ReadKeystore(keystoreJson);
            if (string.Equals(keystore.ProtectionMode, "passkey", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("This wallet is protected by passkey. Use passkey unlock.");
            }

            byte[] salt = string.IsNullOrEmpty(keystore.Salt) ? Array.Empty<byte>() : Convert.FromBase64String(keystore.Salt);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
            byte[] aesKey = pbkdf2.GetBytes(32);
            return DecryptPrivateKeyWithAesKey(keystore, aesKey, "Wrong wallet password or broken session wallet.");
        }

        private async Task<string> DecryptPrivateKeyFromPasskeyKeystoreAsync(string keystoreJson)
        {
            var keystore = ReadKeystore(keystoreJson);
            if (!string.Equals(keystore.ProtectionMode, "passkey", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("This wallet is not passkey-protected.");
            }

            string unwrapJson = await _jsRuntime.InvokeAsync<string>(
                "nexusPasskey.verifyAndUnwrapKeyJson",
                keystore.PasskeyCredentialId,
                keystore.PasskeyWrappedKey);
            var (success, unwrappedKeyBase64) = ReadPasskeyUnwrapResult(unwrapJson);
            if (!success || string.IsNullOrWhiteSpace(unwrappedKeyBase64))
            {
                throw new Exception("Passkey verification failed.");
            }

            return DecryptPrivateKeyWithAesKey(
                keystore,
                Convert.FromBase64String(unwrappedKeyBase64),
                "Passkey wallet decryption failed.");
        }

        private static string DecryptPrivateKeyWithAesKey(KeystoreModel keystore, byte[] aesKey, string errorMessage)
        {
            byte[] iv = Convert.FromBase64String(keystore.Iv);
            byte[] ciphertext = Convert.FromBase64String(keystore.Ciphertext);

            try
            {
                var engine = new AesEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(aesKey);
                var parameters = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, parameters);

                byte[] decryptedBytes = new byte[cipher.GetOutputSize(ciphertext.Length)];
                int len = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, decryptedBytes, 0);
                int finalLen = cipher.DoFinal(decryptedBytes, len);
                byte[] actualDecryptedBytes = new byte[len + finalLen];
                Array.Copy(decryptedBytes, actualDecryptedBytes, len + finalLen);
                return Encoding.UTF8.GetString(actualDecryptedBytes);
            }
            catch
            {
                throw new Exception(errorMessage);
            }
        }

        public async Task Logout()
        {
            _privateKey = "";
            _sessionKeystore = "";
            PublicKey = null;
            UserName = "Guest";
            CurrentMnemonic = null;

            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_user");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_pub");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_priv");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_session_wallet");
        }

        private static (string CredentialId, string WrappedKey) ReadPasskeyWrapResult(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string credentialId = root.TryGetProperty("credentialId", out var credential)
                ? credential.GetString() ?? string.Empty
                : string.Empty;
            string wrappedKey = root.TryGetProperty("wrappedKey", out var wrapped)
                ? wrapped.GetString() ?? string.Empty
                : string.Empty;

            return (credentialId, wrappedKey);
        }

        private static (bool Success, string UnwrappedKeyBase64) ReadPasskeyUnwrapResult(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
            string unwrappedKey = root.TryGetProperty("unwrappedKeyBase64", out var key)
                ? key.GetString() ?? string.Empty
                : string.Empty;

            return (success, unwrappedKey);
        }

        private static KeystoreModel ReadKeystore(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new KeystoreModel
                {
                    Version = root.TryGetProperty("Version", out var version) ? version.GetInt32() : 1,
                    ProtectionMode = GetString(root, "ProtectionMode", "password"),
                    Address = GetString(root, "Address"),
                    Ciphertext = GetString(root, "Ciphertext"),
                    Iv = GetString(root, "Iv"),
                    Salt = GetString(root, "Salt"),
                    PasskeyCredentialId = GetString(root, "PasskeyCredentialId"),
                    PasskeyWrappedKey = GetString(root, "PasskeyWrappedKey")
                };
            }
            catch
            {
                throw new Exception("Wrong format of keystore.");
            }
        }

        private static string GetString(JsonElement root, string propertyName, string fallback = "")
        {
            return root.TryGetProperty(propertyName, out var value)
                ? value.GetString() ?? fallback
                : fallback;
        }
    }
}
