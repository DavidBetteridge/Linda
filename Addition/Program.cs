using TupleClient;

var keysDir = Path.Combine(Directory.GetCurrentDirectory(), "key");
if (args.Length == 1)
    keysDir = args[0];

if (!Directory.Exists(keysDir))
{
    Console.WriteLine("Key directory does not exist");
    return;
}

var keys = Directory.GetFiles(keysDir, "*.pem");
if (keys.Length != 1)
{
    Console.WriteLine("Key directory must contact EXACTLY ONE pem file");
    return;
}

var keyId = Path.GetFileNameWithoutExtension(keys[0]);
var privateKey = await File.ReadAllTextAsync(keys[0]);


// Given the numbers 1...numCount this example will sum them all together.
const int numCount = 100;

// For this example, we are going to create a tuple space with a unique name, starting addition
var spaceName = "addition" + Guid.NewGuid();

var host = Environment.GetEnvironmentVariable("TUPLE_SERVER_HOST") ?? "127.0.0.1";
var portStr = Environment.GetEnvironmentVariable("TUPLE_SERVER_PORT");
if (!int.TryParse(portStr, out var port))
    port = 8080;

// Connect to the tuple space server.
// When running on different computers, don't use a local address!
using var client = new TupleSpaceClient(host, port, spaceName, keyId, privateKey, serverCertificateCustomValidationCallback: (sender, certificate, chain, sslPolicyErrors) => true);

// We write all the numbers we wish to add along with some meta-data and tokens to the tuple space.
await SetupData(client, numCount);


// Now send the code in RemoteCode.PerformAddition to taskCount different machines.  
// This is done by creating taskCount tuples in an expressions tuple-space.  The contents
// of the tuple is the code in RemoteCode.PerformAddition 
Console.WriteLine("Adding");
var taskCount = 10;
var tasks = new Task[taskCount - 1];
for (var i = 0; i < taskCount - 1; i++)
    tasks[i] = client.RunRemotelyAsync(RemoteCode.PerformAddition, RemoteCode.TupleDefinitions, i);
await Task.WhenAll(tasks);


// When the final addition has completed, the result is written to the tuple (total, *)
Console.WriteLine("Waiting for result...");
var finalResult = await client.InAsync<ValueTuple>(Wildcard.Any, numCount);
Console.WriteLine($"Final Result: {finalResult.Value}");

while (true)
{
    var stat = await client.InpAsync<StatsTuple>(Wildcard.Any, Wildcard.Any);
    if (stat is null) break;
    Console.WriteLine($"Client {stat.Value.ProcessName} processed {stat.Value.Counter}");
}

async Task SetupData(TupleSpaceClient tupleSpaceClient, int size)
{
    using var _ = new BulkOutScope(tupleSpaceClient);

    // The numbers to add
    for (var i = 1; i <= size; i++)
        await tupleSpaceClient.OutAsync(new ValueTuple { Value = i, Count = 1 });

    // Each pair of numbers needs a work token
    for (var i = 0; i < size - 1; i++)
        await tupleSpaceClient.OutAsync(new WorkTuple( ));

}

[TupleDefinition]
public readonly struct ValueTuple
{
    public required int Value { get; init; }
    public required int Count { get; init; }
}

[TupleDefinition]
public readonly struct WorkTuple;


[TupleDefinition]
public readonly struct StatsTuple
{
    public required int Counter { get; init; }
    public required string ProcessName { get; init; }
}

public static class AdditionLogic
{
    [RemoteEval]
    public static async Task PerformAddition(TupleSpaceClient c, string processName)
    {
        var counter = 0;
        while (true)
        {
            var token = await c.InpAsync<WorkTuple>();
            if (token is null) break;
            
            var lhs = await c.InAsync<ValueTuple>(Wildcard.Any, Wildcard.Any);
            var rhs = await c.InAsync<ValueTuple>(Wildcard.Any, Wildcard.Any);
            
            var total = lhs.Value + rhs.Value;
            var count = lhs.Count + rhs.Count;
            
            await c.OutAsync(new ValueTuple { Value = total, Count = count });

            counter++;
        }
        await c.OutAsync(new StatsTuple { Counter = counter, ProcessName = processName });
    }
}