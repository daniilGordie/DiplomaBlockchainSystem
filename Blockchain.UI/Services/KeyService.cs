using Microsoft.JSInterop;
using System;
using System.Text;
using System.Threading.Tasks;

using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.X509;

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
            CurrentMnemonic = null;
            UserName = userName;
            await DeriveAndSaveKeys(mnemonic, userName);
            return true;
        }

        public async Task<bool> LoadFromStorage()
        {
            try
            {
                var storedUser = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_user");
                var storedPriv = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_priv");
                var storedPub = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nexus_pub");

                if (!string.IsNullOrEmpty(storedPriv) && !string.IsNullOrEmpty(storedPub))
                {
                    _privateKey = storedPriv;
                    PublicKey = storedPub;
                    UserName = storedUser ?? "Anonymous";
                    return true;
                }
            }
            catch { }
            return false;
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
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_priv", _privateKey);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_pub", PublicKey);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nexus_login_time", DateTime.UtcNow.ToString("O"));
        }

        public string SignData(string data)
        {
            if (!IsLoggedIn || _privateKey == null) throw new InvalidOperationException("User not logged in.");

            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            byte[] privateKeyBytes = Convert.FromBase64String(_privateKey);

            var curve = SecNamedCurves.GetByName("secp256r1");
            var domainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
            var privKeyParams = new ECPrivateKeyParameters(new BigInteger(1, privateKeyBytes), domainParams);

            var signer = new ECDsaSigner();
            signer.Init(true, privKeyParams);

            var digest = new Sha256Digest();
            digest.BlockUpdate(dataBytes, 0, dataBytes.Length);
            byte[] hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);

            var signature = signer.GenerateSignature(hash);
            var r = signature[0].ToByteArrayUnsigned();
            var s = signature[1].ToByteArrayUnsigned();

            byte[] p1363Signature = new byte[64];
            Buffer.BlockCopy(r, 0, p1363Signature, 32 - r.Length, r.Length);
            Buffer.BlockCopy(s, 0, p1363Signature, 64 - s.Length, s.Length);

            return Convert.ToBase64String(p1363Signature);
        }

        public async Task Logout()
        {
            _privateKey = null;
            PublicKey = null;
            UserName = "Guest";
            CurrentMnemonic = null;

            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_user");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_priv");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_pub");
            await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nexus_login_time");
        }
    }
}