using System.Security.Cryptography;

namespace RemotePrint.Client;

public static class EncryptionService
{
    public static EncryptedPayload Encrypt(byte[] data, string recipientPublicKey)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, ciphertext, tag);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientPublicKey), out _);
        var wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        CryptographicOperations.ZeroMemory(key);
        return new EncryptedPayload
        {
            WrappedKey = Convert.ToBase64String(wrapped), Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag), Ciphertext = Convert.ToBase64String(ciphertext)
        };
    }

    public static byte[] Decrypt(EncryptedPayload payload, RSA privateKey)
    {
        using (privateKey)
        {
            var key = privateKey.Decrypt(Convert.FromBase64String(payload.WrappedKey), RSAEncryptionPadding.OaepSHA256);
            try
            {
                var ciphertext = Convert.FromBase64String(payload.Ciphertext);
                var plaintext = new byte[ciphertext.Length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(Convert.FromBase64String(payload.Nonce), ciphertext, Convert.FromBase64String(payload.Tag), plaintext);
                return plaintext;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }
}
