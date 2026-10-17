using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OnlyShell.Core.Sync;

namespace OnlyShell.Infrastructure.Sync;

/// <summary>Encrypts sync documents before they leave the device.</summary>
public sealed class AesGcmSyncPayloadProtector : ISyncPayloadProtector
{
    private const int EnvelopeVersion = 1;
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Protect(string plaintext, string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var clearBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[clearBytes.Length];
        var tag = new byte[TagSize];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, clearBytes, cipherBytes, tag, Encoding.UTF8.GetBytes("OnlyShell.Sync.v1"));
            var envelope = new EncryptedSyncEnvelope(
                EnvelopeVersion,
                Iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(cipherBytes),
                Convert.ToBase64String(tag));
            return JsonSerializer.Serialize(envelope, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public string Unprotect(string encryptedEnvelope, string passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedEnvelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);

        var envelope = JsonSerializer.Deserialize<EncryptedSyncEnvelope>(encryptedEnvelope, JsonOptions)
            ?? throw new InvalidDataException("同步数据格式无效。");
        if (envelope.Version != EnvelopeVersion || envelope.Iterations is < 100_000 or > 2_000_000)
            throw new InvalidDataException("同步数据使用了不支持的加密版本或参数。");

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var cipherBytes = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize)
            throw new InvalidDataException("同步数据的加密字段长度不正确。");

        var clearBytes = new byte[cipherBytes.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, envelope.Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, clearBytes, Encoding.UTF8.GetBytes("OnlyShell.Sync.v1"));
            return Encoding.UTF8.GetString(clearBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private sealed record EncryptedSyncEnvelope(int Version, int Iterations, string Salt, string Nonce, string Ciphertext, string Tag);
}
