using System.Security.Cryptography;
using TupleClient;

namespace TupleServer.Tests;

public class KeyManagementTests : AuthTestBase
{
    private const int Port = 8085;

    [SetUp]
    public void Setup()
    {
        StartServer(Port);
    }

    [TearDown]
    public async Task TearDown()
    {
        await StopServer();
    }

    [Test]
    public async Task TestUploadAndUseNewKey()
    {
        // 1. Authenticate with existing key and upload new key
        using var client = CreateClient("management-test");
        
        using var newRsa = RSA.Create(2048);
        const string newKeyId = "newkey";
        var newPrivateKeyPem = newRsa.ExportRSAPrivateKeyPem();
        var newPublicKeyPem = newRsa.ExportRSAPublicKeyPem();

        await client.UploadPublicKeyAsync(newKeyId, newPublicKeyPem);

        // 2. Try to connect with the new key
        using var newClient = new TupleSpaceClient("127.0.0.1", Port, "management-test", newKeyId, newPrivateKeyPem);
        await newClient.OutAsync("test", "new-key-works");
        
        var result = await newClient.InAsync("test", "new-key-works");
        Assert.That(result, Is.EqualTo(new[] { "test", "new-key-works" }));
    }

    [Test]
    public async Task TestRevokeKey()
    {
        // 1. Authenticate with existing key and upload new key
        using var client = CreateClient("management-test");
        
        using var newRsa = RSA.Create(2048);
        var newKeyId = "revokeme";
        var newPrivateKeyPem = newRsa.ExportRSAPrivateKeyPem();
        var newPublicKeyPem = newRsa.ExportRSAPublicKeyPem();

        await client.UploadPublicKeyAsync(newKeyId, newPublicKeyPem);

        // 2. Verify it works
        using (var newClient = new TupleSpaceClient("127.0.0.1", Port, "management-test", newKeyId, newPrivateKeyPem))
        {
            await newClient.OutAsync("test", "works-before-revoke");
        }

        // 3. Revoke the key
        await client.RevokePublicKeyAsync(newKeyId);

        // 4. Try to connect with revoked key - should fail
        using var revokedClient = new TupleSpaceClient("127.0.0.1", Port, "management-test", newKeyId, newPrivateKeyPem);
        Assert.ThrowsAsync<Exception>(async () => await revokedClient.OutAsync("test", "should-fail"));
    }

    [Test]
    public async Task TestCannotUploadWithoutAuth()
    {
        // Start a server without key management first to show it's different, 
        // but here we just use the one already started which REQUIRES auth.
        
        // Try to connect without credentials
        using var client = new TupleSpaceClient("127.0.0.1", Port, "unauth-test");
        
        // This should fail because the server requires AUTH and the client didn't provide it.
        // Actually TupleSpaceClient.EnsureConnectedAsync will try to send AUTH if keyId is provided.
        // If not provided, it sends nothing and server expects AUTH.
        
        Assert.ThrowsAsync<Exception>(async () => await client.UploadPublicKeyAsync("fail", "pem"));
    }
}
