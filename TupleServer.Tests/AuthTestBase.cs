using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using TupleClient;

namespace TupleServer.Tests;

public abstract class AuthTestBase
{
    protected CancellationTokenSource _cts;
    protected Task _serverTask;
    protected int _port;
    protected string _tempKeysDir;
    protected string _testKeyId = "testkey";
    protected string _testPrivateKeyPem;
    protected string _testPublicKeyPem;
    protected X509Certificate2? _serverCert;
    protected bool _useTls = false;

    protected void StartServer(int port)
    {
        _port = port;
        _useTls = true; // Always use TLS now
        _tempKeysDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempKeysDir);

        using var rsa = RSA.Create(2048);
        _testPrivateKeyPem = rsa.ExportRSAPrivateKeyPem();
        _testPublicKeyPem = rsa.ExportRSAPublicKeyPem();
        File.WriteAllText(Path.Combine(_tempKeysDir, $"{_testKeyId}.pub"), _testPublicKeyPem);

        var request = new CertificateRequest("cn=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _serverCert = request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));

        _cts = new CancellationTokenSource();
        var server = new TcpServer(_port, new ServerStatistics(), _serverCert, keysDirectory: _tempKeysDir);
        _serverTask = server.StartAsync(_cts.Token);
    }

    protected async Task StopServer()
    {
        _cts.Cancel();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException) { }
        _cts.Dispose();

        if (Directory.Exists(_tempKeysDir))
        {
            Directory.Delete(_tempKeysDir, true);
        }
    }

    protected TupleSpaceClient CreateClient(string spaceName)
    {
        return new TupleSpaceClient("127.0.0.1", _port, spaceName, _testKeyId, _testPrivateKeyPem, (sender, certificate, chain, sslPolicyErrors) => true);
    }
}
