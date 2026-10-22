using System.Security.Cryptography;

namespace TupleServer.Auth;

public class KeyManager
{
    private readonly string _keysDirectory;
    private readonly Dictionary<string, RSA> _publicKeys = new();

    public KeyManager(string keysDirectory)
    {
        _keysDirectory = keysDirectory;
        LoadKeys();
    }

    private void LoadKeys()
    {
        if (!Directory.Exists(_keysDirectory))
        {
            Directory.CreateDirectory(_keysDirectory);
        }

        lock (_publicKeys)
        {
            _publicKeys.Clear();
            foreach (var file in Directory.GetFiles(_keysDirectory, "*.pub"))
            {
                var keyId = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(File.ReadAllText(file));
                    _publicKeys[keyId] = rsa;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load key {file}: {ex.Message}");
                }
            }
        }
    }

    public RSA? GetPublicKey(string keyId)
    {
        lock (_publicKeys)
        {
            return _publicKeys.TryGetValue(keyId, out var rsa) ? rsa : null;
        }
    }

    public void AddPublicKey(string keyId, string publicKeyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        
        var filePath = Path.Combine(_keysDirectory, $"{keyId}.pub");
        File.WriteAllText(filePath, publicKeyPem);

        lock (_publicKeys)
        {
            _publicKeys[keyId] = rsa;
        }
    }

    public void RevokePublicKey(string keyId)
    {
        var filePath = Path.Combine(_keysDirectory, $"{keyId}.pub");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        lock (_publicKeys)
        {
            _publicKeys.Remove(keyId);
        }
    }

    public bool VerifyResponse(string keyId, byte[] challenge, byte[] signature)
    {
        var rsa = GetPublicKey(keyId);
        if (rsa == null) return false;

        return rsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
