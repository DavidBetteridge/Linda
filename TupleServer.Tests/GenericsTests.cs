using TupleClient;

namespace TupleServer.Tests;

public class GenericsTests : AuthTestBase
{
    private const int Port = 8082;

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

    public struct MyTuple
    {
        public string Key;
        public int Value;
        public string Metadata;
    }

    public struct AnotherTuple
    {
        public int A;
        public int B;
    }

    [Test]
    public async Task TestOutAsyncGeneric()
    {
        using var client = CreateClient("test-generics");

        var myTuple = new MyTuple { Key = "test", Value = 123, Metadata = "meta" };
        
        await client.OutAsync<MyTuple>(myTuple);

        var result = await client.InAsync("MyTuple", "test", "123", "meta");
        Assert.That(result, Is.EqualTo(new[] { "MyTuple", "test", "123", "meta" }));
    }

    [Test]
    public async Task TestGenericWithBulkOut()
    {
        using var client = CreateClient("test-bulk-generics");

        using (new BulkOutScope(client))
        {
            await client.OutAsync(new AnotherTuple { A = 1, B = 2 });
            await client.OutAsync(new AnotherTuple { A = 3, B = 4 });
        }

        var res1 = await client.InAsync("AnotherTuple", "1", "2");
        var res2 = await client.InAsync("AnotherTuple", "3", "4");

        Assert.That(res1, Is.EqualTo(new[] { "AnotherTuple", "1", "2" }));
        Assert.That(res2, Is.EqualTo(new[] { "AnotherTuple", "3", "4" }));
    }

    [Test]
    public async Task TestInAsyncGeneric()
    {
        using var client = CreateClient("test-in-generic");

        await client.OutAsync("MyTuple", "test", "456", "extra");

        var result = await client.InAsync<MyTuple>("test", "456", "*");
        Assert.That(result.Key, Is.EqualTo("test"));
        Assert.That(result.Value, Is.EqualTo(456));
        Assert.That(result.Metadata, Is.EqualTo("extra"));
    }
}
