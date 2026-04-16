using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Blockchain.UI.Services
{
    public class MnemonicService
    {
        public string GenerateMnemonic()
        {
            if (Bip39Words.Wordlist.Length < 2048)
            {
                return GenerateSimpleRandomMnemonic();
            }

            return GenerateBip39Mnemonic();
        }

        public byte[] GenerateSeed(string mnemonic, string passphrase = "")
        {
            if (!ValidateMnemonic(mnemonic))
            {
                throw new ArgumentException("Invalid mnemonic phrase provided.");
            }

            byte[] salt = Encoding.UTF8.GetBytes("mnemonic" + passphrase);
            byte[] password = Encoding.UTF8.GetBytes(mnemonic);

            return Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                2048,
                HashAlgorithmName.SHA512,
                64 
            );
        }

        private string GenerateBip39Mnemonic()
        {
            byte[] entropy = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(entropy);
            }

            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(entropy);
            }

            var bits = new StringBuilder();
            foreach (var b in entropy)
            {
                bits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));
            }

            bits.Append(Convert.ToString(hash[0], 2).PadLeft(8, '0').Substring(0, 4));

            var words = new List<string>();
            string bitsString = bits.ToString();

            for (int i = 0; i < 12; i++)
            {
                string chunk = bitsString.Substring(i * 11, 11);
                int index = Convert.ToInt32(chunk, 2);
                words.Add(Bip39Words.Wordlist[index]);
            }

            return string.Join(" ", words);
        }

        private string GenerateSimpleRandomMnemonic()
        {
            var words = new List<string>();
            var random = new Random();
            int maxIndex = Bip39Words.Wordlist.Length;

            for (int i = 0; i < 12; i++)
            {
                int index = random.Next(0, maxIndex);
                words.Add(Bip39Words.Wordlist[index]);
            }

            return string.Join(" ", words);
        }

        public bool ValidateMnemonic(string mnemonic)
        {
            if (string.IsNullOrWhiteSpace(mnemonic)) return false;

            var words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 12) return false;

            foreach (var word in words)
            {
                if (!Bip39Words.Wordlist.Contains(word)) return false;
            }

            return true;
        }
    }
}