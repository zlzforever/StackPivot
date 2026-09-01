using System.Security.Cryptography;
using System.Text;

namespace StackPivot.Control.Infrastructure.Security;

public static class RequiredSecretConfiguration
{
    public static byte[] ReadBase64(string? value, string name, int expectedLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedLength);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required secret configuration '{name}' is missing.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"Required secret configuration '{name}' is not valid base64.", exception);
        }

        if (decoded.Length != expectedLength)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new InvalidOperationException($"Required secret configuration '{name}' has an invalid length.");
        }

        return decoded;
    }
}

public interface ISecretKeyProvider
{
    byte[] GetKey(string keyId);
}

public sealed class StaticSecretKeyProvider : ISecretKeyProvider
{
    private readonly byte[] key;

    public StaticSecretKeyProvider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-GCM key must contain 32 bytes.", nameof(key));
        }

        this.key = key.ToArray();
    }

    public byte[] GetKey(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        return key.ToArray();
    }
}

public interface IGitCredentialProtector
{
    string Protect(string token);
    string Protect(string token, string keyId);
    byte[] Unprotect(string encrypted, string keyId);
}

public sealed class AesGcmGitCredentialProtector : IGitCredentialProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly ISecretKeyProvider keyProvider;
    private readonly string defaultKeyId;

    public AesGcmGitCredentialProtector(byte[] key, string defaultKeyId = "default")
        : this(new StaticSecretKeyProvider(key), defaultKeyId)
    {
    }

    public AesGcmGitCredentialProtector(ISecretKeyProvider keyProvider, string defaultKeyId = "default")
    {
        this.keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        if (string.IsNullOrWhiteSpace(defaultKeyId))
        {
            throw new ArgumentException("A default key id is required.", nameof(defaultKeyId));
        }

        this.defaultKeyId = defaultKeyId;
    }

    public string Protect(string token)
    {
        return Protect(token, defaultKeyId);
    }

    public string Protect(string token, string keyId)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var key = keyProvider.GetKey(keyId);
        var plaintext = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            var payload = new byte[NonceSize + ciphertext.Length + TagSize];
            nonce.CopyTo(payload, 0);
            ciphertext.CopyTo(payload, NonceSize);
            tag.CopyTo(payload, NonceSize + ciphertext.Length);
            return Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public byte[] Unprotect(string encrypted, string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encrypted);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encrypted);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Encrypted Git credential is invalid.", exception);
        }

        if (payload.Length < NonceSize + TagSize)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new CryptographicException("Encrypted Git credential is invalid.");
        }

        var key = keyProvider.GetKey(keyId);
        var nonce = payload[..NonceSize];
        var ciphertextLength = payload.Length - NonceSize - TagSize;
        var ciphertext = payload.AsSpan(NonceSize, ciphertextLength).ToArray();
        var tag = payload.AsSpan(NonceSize + ciphertextLength, TagSize).ToArray();
        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("Encrypted Git credential cannot be decrypted.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }
}
