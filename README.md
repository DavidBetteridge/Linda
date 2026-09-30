# Linda Tuple Space for .NET

A distributed coordination system based on the Linda coordination language, featuring remote execution via Roslyn scripting and C# Source Generators.

## Architecture

The system consists of four main components:

1.  **TupleServer**: A TCP-based server that manages multiple named "tuple spaces". It supports atomic coordination primitives like `IN` (consume), `OUT` (produce), and `RD` (read). It also broadcasts events over UDP for visualization.
2.  **TupleClient**: A library providing the `TupleSpaceClient` for interacting with the server.
3.  **ExpressionRunner**: A distributed worker that polls an `expressions` tuple space for C# code strings to execute against specific tuple spaces.
4.  **TupleClient.Generators**: A C# Source Generator that allows developers to write type-safe code for remote execution.
5.  **TupleVisualizer**: A native Avalonia application for real-time visualization of tuple spaces and processes.

## Core Operations (Linda Primitives)

The `TupleSpaceClient` provides the following operations. Both string-based and generic type-safe APIs are supported.

### String-Based API

| Operation | Description |
| :--- | :--- |
| `OutAsync(params string[] tuple)` | **Produce**: Adds a tuple to the space. |
| `InAsync(params string[] pattern)` | **Consume**: Blocks until a tuple matching the pattern is available, then removes and returns it. |
| `RdAsync(params string[] pattern)` | **Read**: Blocks until a tuple matching the pattern is available, then returns it without removing it. |
| `InpAsync(params string[] pattern)` | **Probe Consume**: Non-blocking version of `IN`. |
| `RdpAsync(params string[] pattern)` | **Probe Read**: Non-blocking version of `RD`. |

*Patterns support wildcards using the `"*"` string.*

### Generic API (Type-Safe)

The generic API uses C# structs to represent tuples. When using generics, the name of the struct is automatically prepended as the first element of the tuple in the tuple space.

```csharp
[TupleDefinition]
public struct TaskTuple 
{
    public string Id { get; set; }
    public int Priority { get; set; }
}

// Writes ("TaskTuple", "task-1", "10") to the space
await client.OutAsync(new TaskTuple { Id = "task-1", Priority = 10 });

// Reads back a TaskTuple with exact values
var task = await client.InAsync<TaskTuple>("task-1", 10);

// Use Wildcard.Any for pattern matching
var anyTask = await client.InAsync<TaskTuple>(Wildcard.Any, Wildcard.Any);
```

Generic methods include `OutAsync<T>`, `InAsync<T>`, `RdAsync<T>`, `InpAsync<T>`, and `RdpAsync<T>`. Structs used with the generic API should be marked with the `[TupleDefinition]` attribute if they need to be available for remote evaluation.

### Live Tuples (`EvalAsync`)

The `EvalAsync` operation creates a **Live Tuple**. A live tuple contains a mixture of normal values and expressions (delegates) to be evaluated.

The evaluation happens concurrently on background threads. Once all expressions are evaluated, the results replace the expressions in the tuple, and the final "normal" tuple is published to the tuple space.

```csharp
[TupleDefinition]
public struct MyTuple {
    public string Name { get; set; }
    public object Value { get; set; }
}

// Start concurrent evaluation
await client.EvalAsync(new MyTuple {
    Name = "Task1",
    Value = new Func<Task<int>>(async () => {
        await Task.Delay(100);
        return 42;
    })
});

// This will block until the evaluation is complete and the tuple is published
var result = await client.InAsync<MyTuple>("Task1", Wildcard.Any);
// result.Value will be "42"
```

### Wildcard Matching

For pattern matching in generic methods, use `Wildcard.Any` instead of the string `"*"`. This provides type safety and avoids ambiguity when a tuple value might actually be the literal string `"*"`.

```csharp
// Match any TaskTuple regardless of Id or Priority
var task = await client.InAsync<TaskTuple>(Wildcard.Any, Wildcard.Any);

// Match a specific Id with any Priority
var task = await client.InAsync<TaskTuple>("task-1", Wildcard.Any);
```

### Compile-Time Validation

The `TupleClient.Generators` package includes a Roslyn analyzer that validates generic tuple method calls at compile time:

- **TUPLE001**: Reports an error if the number of pattern parameters doesn't match the number of properties in the tuple struct.
- **TUPLE002**: Reports an error if a parameter type doesn't match the corresponding property type (unless `Wildcard.Any` is used).

```csharp
[TupleDefinition]
public struct TaskTuple 
{
    public string Id { get; set; }
    public int Priority { get; set; }
}

// ✓ Correct: 2 parameters matching 2 properties with correct types
await client.InAsync<TaskTuple>("task-1", 10);

// ✓ Correct: Wildcard.Any can match any type
await client.InAsync<TaskTuple>(Wildcard.Any, Wildcard.Any);

// ✗ Error TUPLE001: Wrong number of parameters
await client.InAsync<TaskTuple>("task-1");

// ✗ Error TUPLE002: Type mismatch - Priority expects int, not string
await client.InAsync<TaskTuple>("task-1", "high");
```

## Bulk Operations

To improve performance when adding many tuples, use the `BulkOutScope`:

```csharp
using (var scope = new BulkOutScope(client))
{
    await client.OutAsync("key", "value1");
    await client.OutAsync("key", "value2");
    // Only OUT operations are allowed in this scope
}
```

## Remote Execution (`RunRemotelyAsync`)

The system supports a powerful `RunRemotelyAsync` mechanism that allows code to be shipped to a remote `ExpressionRunner` for execution close to the data.

### 1. Define Remote Logic
Mark a static method with the `[RemoteEval]` attribute. The method must take a `TupleSpaceClient` as its first argument (usually named `c`).

```csharp
public static class MyLogic
{
    [RemoteEval]
    public static async Task PerformWork(TupleSpaceClient c)
    {
        var data = await c.InAsync("data", "*");
        // ... process data ...
        await c.OutAsync("result", "processed");
    }
}
```

### 2. Invoke Remotely
The Source Generator automatically extracts the method body as a string and puts it in a `RemoteCode` class. Use `RunRemotelyAsync` to send this code to the runner:

```csharp
await client.RunRemotelyAsync(RemoteCode.PerformWork);
```

## Getting Started

### 1. Start the Server
```bash
dotnet run --project TupleServer/TupleServer.csproj
```

### 2. Start the Expression Runner
You can start multiple runners to handle parallel workloads.
```bash
dotnet run --project ExpressionRunner/Program.cs
```

### 3. Run the Addition Example
The `Addition` project demonstrates summing numbers 1 to 1000 by distributing addition tasks across the runners.
```bash
dotnet run --project Addition/Program.cs
```

### 4. Start the Visualizer (Optional)
Start the visualizer to see the interaction between processes and tuple spaces in real-time.
```bash
dotnet run --project TupleVisualizer/TupleVisualizer.csproj
```

## Tuple Space Visualizer

The `TupleVisualizer` is a native Avalonia application that provides a graphical view of the distributed system.

### Features
- **Graph Representation**: Tuple spaces and processes are shown as nodes in a graph.
- **Interactive**: Nodes can be dragged to organize the layout.
- **Real-time Status**: Lines between nodes indicate the current state of communication:
    - **Green**: Writing to a tuple space (`OUT`, `OUTBULK`).
    - **Blue**: Reading from a tuple space (`RD`, `IN`, `RDP`, `INP`).
    - **Orange (Dashed)**: Process is currently **blocked** waiting for a tuple.
- **Automatic Cleanup**: Processes are removed from the visualizer when they disconnect.

### Communication
The `TupleServer` broadcasts events over **UDP port 9999**. The visualizer listens on this port. Since UDP is connectionless and "fire-and-forget", the visualization overhead on the server is minimal. The visualizer includes rate-limiting logic to handle high-frequency event bursts without freezing the UI.

## Project Structure

- `TupleServer/`: The core TCP server.
- `TupleClient/`: The shared library and `TupleSpaceClient`.
- `TupleClient.Generators/`: The Roslyn Source Generator.
- `ExpressionRunner/`: The worker process using `Microsoft.CodeAnalysis.CSharp.Scripting`.
- `Addition/`: An example application utilizing all components.
- `SudokuSolver/`: An example demonstrating recursive solving via distributed tasks.
- `DataFlow/`: A data pipeline example showing Stage 0 (generation), Stage 1 (doubling), and Stage 2 (incrementing).
- `TupleVisualizer/`: The Avalonia-based visualization tool.
- `TupleServer.Tests/`: Integration tests for the core protocol.
