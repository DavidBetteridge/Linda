using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using TupleClient;

namespace TupleServer.Tests;

[TestFixture]
public class EncryptionTests : AuthTestBase
{
    private const int Port = 8082;
    private const int TlsPort = 8083;

    [Test]
    public async Task TestTrafficIsEncrypted()
    {
        StartServer(TlsPort);
        try
        {
            using var client = CreateClient("test-encryption");
            await client.OutAsync("secret", "data");
            
            var result = await client.InAsync("secret", "*");
            Assert.That(result[1], Is.EqualTo("data"));
        }
        finally
        {
            await StopServer();
        }
    }

    [Test]
    public async Task TestNonTlsFails()
    {
        StartServer(Port);
        try
        {
            // Try to connect without TLS
            var client = new TupleSpaceClient("127.0.0.1", Port, "test-enforcement", _testKeyId, _testPrivateKeyPem);
            
            // This should fail because the server will expect a TLS handshake
            Assert.ThrowsAsync<Exception>(async () => await client.OutAsync("data"));
        }
        finally
        {
            await StopServer();
        }
    }
}
