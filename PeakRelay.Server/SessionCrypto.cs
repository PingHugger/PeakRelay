using System;
using System.Numerics;
using System.Security.Cryptography;

namespace PeakRelay.Server;

/// <summary>
/// Server side of Photon's PayloadEncryption, byte-compatible with the shipped client
/// (references/Photon3Unity3D-src/Photon.SocketServer.Security/DiffieHellmanCryptoProvider.cs):
///   - DH with Oakley 768-bit prime, generator 22 (constants shipped in the client DLL)
///   - shared key -> SHA256 -> AES-256 (Rijndael) CBC, PKCS7, zero IV
///   - big-int wire format: big-endian, minimal length (client flips MS little-endian arrays)
/// </summary>
public sealed class SessionCrypto
{
    // Photon.SocketServer.Security/OakleyGroups.cs (shipped in the client's DLL)
    private static readonly byte[] OakleyPrime768 =
    {
        255, 255, 255, 255, 255, 255, 255, 255, 32, 54,
        58, 166, 233, 66, 76, 244, 198, 126, 94, 98,
        118, 181, 133, 228, 69, 194, 81, 109, 109, 53,
        225, 79, 55, 20, 95, 242, 109, 10, 43, 48,
        27, 67, 58, 205, 179, 25, 149, 239, 221, 4,
        52, 142, 121, 8, 74, 81, 34, 155, 19, 59,
        166, 190, 11, 2, 116, 204, 103, 138, 8, 78,
        2, 41, 209, 28, 220, 128, 139, 98, 198, 196,
        52, 194, 104, 33, 162, 218, 15, 201, 255, 255,
        255, 255, 255, 255, 255, 255, 0
    };

    private const int Generator = 22;

    private static readonly BigInteger Prime = new(OakleyPrime768);
    private static readonly BigInteger Root = new(Generator);

    private readonly BigInteger _secret;
    private readonly byte[] _publicKey;
    private Aes? _aes;

    public SessionCrypto()
    {
        _secret = GenerateRandomSecret();
        _publicKey = ToPhotonBytes(BigInteger.ModPow(Root, _secret, Prime));
    }

    /// <summary>Server public key (Photon big-int wire bytes) for the key-exchange response.</summary>
    public byte[] PublicKey => _publicKey;

    /// <summary>Derive the AES key from the client's public key. Call once per session.</summary>
    public void DeriveSharedKey(byte[] clientPublicKey)
    {
        var clientPub = FromPhotonBytes(clientPublicKey);
        var shared = BigInteger.ModPow(clientPub, _secret, Prime);
        var sharedBytes = ToPhotonBytes(shared);

        byte[] aesKey;
        using (var sha = SHA256.Create())
            aesKey = sha.ComputeHash(sharedBytes);

        _aes = Aes.Create();
        _aes.Key = aesKey;
        _aes.IV = new byte[16];
        _aes.Padding = PaddingMode.PKCS7;
        _aes.Mode = CipherMode.CBC;
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        if (_aes == null)
            throw new InvalidOperationException("crypto not established");
        using var encryptor = _aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    public byte[] Decrypt(byte[] ciphertext)
    {
        if (_aes == null)
            throw new InvalidOperationException("crypto not established");
        using var decryptor = _aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>MS little-endian bigint -> Photon big-endian minimal bytes (client convention).</summary>
    private static byte[] ToPhotonBytes(BigInteger value)
    {
        var ms = value.ToByteArray();           // little-endian, may have trailing 0x00
        Array.Reverse(ms);                       // now big-endian
        if (ms.Length > 1 && ms[0] == 0)
        {
            var trimmed = new byte[ms.Length - 1];
            Buffer.BlockCopy(ms, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        return ms;
    }

    private static BigInteger FromPhotonBytes(byte[] photon)
    {
        var ms = (byte[])photon.Clone();
        Array.Reverse(ms);                       // little-endian
        if ((ms[^1] & 0x80) != 0)                // sign bit set: prepend zero byte
        {
            var widened = new byte[ms.Length + 1];
            Buffer.BlockCopy(ms, 0, widened, 0, ms.Length);
            return new BigInteger(widened);
        }
        return new BigInteger(ms);
    }

    private static BigInteger GenerateRandomSecret()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[20]; // 160-bit, as the client does
        rng.GetBytes(bytes);
        var value = new BigInteger(bytes);
        while (value >= Prime - 1 || value < 2)
        {
            rng.GetBytes(bytes);
            value = new BigInteger(bytes);
        }
        return value;
    }
}
