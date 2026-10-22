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

    protected void StartServer(int port)
    {
        _port = port;
        _tempKeysDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempKeysDir);

        using var rsa = RSA.Create(2048);
        _testPrivateKeyPem = rsa.ExportRSAPrivateKeyPem();
        _testPublicKeyPem = rsa.ExportRSAPublicKeyPem();
        File.WriteAllText(Path.Combine(_tempKeysDir, $"{_testKeyId}.pub"), _testPublicKeyPem);

        _cts = new CancellationTokenSource();
        var server = new TcpServer(_port, new ServerStatistics(), keysDirectory: _tempKeysDir);
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
        return new TupleSpaceClient("127.0.0.1", _port, spaceName, _testKeyId, _testPrivateKeyPem);
    }
}
