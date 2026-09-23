using TupleClient;

var spaceName = "dataflow-" + Guid.NewGuid();
using var client = new TupleSpaceClient("127.0.0.1", 8080, spaceName);

Console.WriteLine($"Starting DataFlow example on space: {spaceName}");

// Launch Stage 0 process (worker)
_ = client.RunRemotelyAsync(RemoteCode.ProducerProcess, RemoteCode.TupleDefinitions, 0);

// Launch 2 Stage 1 processes (worker)
_ = client.RunRemotelyAsync(RemoteCode.Stage1Process, RemoteCode.TupleDefinitions, 1);
_ = client.RunRemotelyAsync(RemoteCode.Stage1Process, RemoteCode.TupleDefinitions, 2);

// Launch Stage 2 process (worker)
_ = client.RunRemotelyAsync(RemoteCode.Stage2Process, RemoteCode.TupleDefinitions, 3);

// Main process: Wait for stage 3 tuples and display their values
Console.WriteLine("Main process: Waiting for Stage 3 tuples...");
var nextSequenceNumber = 1;
while (true)
{
    var stage3 = await client.InAsync<Stage3Tuple>(nextSequenceNumber, Wildcard.Any, Wildcard.Any);
    var duration = (DateTime.Now - stage3.CreatedAt).Milliseconds;
    Console.WriteLine($"Main process: {stage3.SequenceNumber}:: {stage3.Number} :: { duration }ms");
    nextSequenceNumber++;
}

[TupleDefinition]
public readonly struct Stage1Tuple
{
    public required int SequenceNumber { get; init; }
    public required int Number { get; init; }
    public required DateTime CreatedAt { get; init; }
}

[TupleDefinition]
public readonly struct Stage2Tuple
{
    public required int SequenceNumber { get; init; }
    public required int Number { get; init; }
    public required DateTime CreatedAt { get; init; }
}

[TupleDefinition]
public readonly struct Stage3Tuple
{
    public required int SequenceNumber { get; init; }
    public required int Number { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public static class DataFlowLogic
{
    [RemoteEval]
    public static async Task ProducerProcess(TupleSpaceClient c, string processName, int workerId)
    {
        var random = new Random();
        var sequenceNumber = 0;
        while (true)
        {
            sequenceNumber++;
            var number = random.Next(1, 1001);
            Console.WriteLine($"Stage 0 ({processName}): Generated {number}");
            await c.OutAsync(new Stage1Tuple { Number = number, SequenceNumber = sequenceNumber, CreatedAt = DateTime.Now});
            await Task.Delay(1000);
        }
    }

    [RemoteEval]
    public static async Task Stage1Process(TupleSpaceClient c, string processName, int workerId)
    {
        var rnd = new Random();
        while (true)
        {
            var tuple = await c.InAsync<Stage1Tuple>(Wildcard.Any,Wildcard.Any,Wildcard.Any);
            
            // Wait a random amount of time, to get the tuples out of order. 
            var delay = rnd.Next(1, 1500);
            await Task.Delay(delay);
            
            var result = tuple.Number * 2;
            await c.OutAsync(new Stage2Tuple { Number = result, 
                SequenceNumber = tuple.SequenceNumber,
                CreatedAt = tuple.CreatedAt
            });
        }
    }

    [RemoteEval]
    public static async Task Stage2Process(TupleSpaceClient c, string processName, int workerId)
    {
        while (true)
        {
            var tuple = await c.InAsync<Stage2Tuple>(Wildcard.Any, Wildcard.Any, Wildcard.Any);
            var result = tuple.Number + 1;
            await c.OutAsync(new Stage3Tuple {
                Number = result, 
                SequenceNumber = tuple.SequenceNumber,
                CreatedAt = tuple.CreatedAt
                
            });
        }
    }
}
