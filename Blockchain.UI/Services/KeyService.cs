using Microsoft.JSInterop;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.X509;
using Blockchain.UI.Models;
using System.Security.Cryptography;
using System.Text.Json;
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
        private string? _privateKey;
        private readonly IJSRuntime _jsRuntime;
        private readonly MnemonicService _mnemonicService;

        public string? PublicKey { get; private set; }
        public string UserName { get; private set; } = "Guest";
        public string? CurrentMnemonic { get; private set; }

        public bool IsLoggedIn => !string.IsNullOrEmpty(_privateKey);

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
        }

        public async Task<bool> LoadFromStorage()
        {
            try
            {
                var storedUser = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_user");
                var storedPub = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_pub");

                if (!string.IsNullOrEmpty(storedUser) && !string.IsNullOrEmpty(storedPub))
                {
                    UserName = storedUser;
                    PublicKey = storedPub;

                    return !string.IsNullOrEmpty(_privateKey);
                }
            }
            catch { }
            return false;
        }

        public string SignData(string data)
        {
            if (string.IsNullOrEmpty(_privateKey))
                throw new InvalidOperationException("Приватный ключ отсутствует в памяти. Требуется повторная авторизация.");

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

            var keystore = new KeystoreModel
            {
                Address = PublicKey,
                Ciphertext = Convert.ToBase64String(ciphertext),
                Iv = Convert.ToBase64String(iv),
                Salt = Convert.ToBase64String(salt)
            };

            return JsonSerializer.Serialize(keystore);
        }

        public string SignDataWithKeystore(string keystoreJson, string password, string dataToSign)
        {
            var keystore = JsonSerializer.Deserialize<KeystoreModel>(keystoreJson);
            if (keystore == null) throw new Exception("Wrong format of keystore.");

            byte[] salt = Convert.FromBase64String(keystore.Salt);
            byte[] iv = Convert.FromBase64String(keystore.Iv);
            byte[] ciphertext = Convert.FromBase64String(keystore.Ciphertext);

            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 10000, HashAlgorithmName.SHA256);
            byte[] aesKey = pbkdf2.GetBytes(32);

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

                string decryptedPrivateKey = Encoding.UTF8.GetString(actualDecryptedBytes);

                string tempBackup = _privateKey;
                _privateKey = decryptedPrivateKey;

                string signature = SignData(dataToSign);

                _privateKey = tempBackup;
                decryptedPrivateKey = new string('*', decryptedPrivateKey.Length);

                return signature;
            }
            catch (Exception)
            {
                throw new Exception("Wrong Keystore password or file is broken.");
            }
        }

        public async Task Logout()
        {
            _privateKey = null;
            PublicKey = null;
            UserName = "Guest";
            CurrentMnemonic = null;

            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_user");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_pub");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_priv");
        }
    }
}