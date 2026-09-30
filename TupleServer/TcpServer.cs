using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TupleServer;

public class TcpServer
{
    private readonly int _port;
    private readonly TupleSpaceManager _manager;
    private readonly ServerStatistics _stats;
    private readonly VisualizerBroadcaster _broadcaster;

    public TcpServer(int port, ServerStatistics stats, VisualizerBroadcaster? broadcaster = null)
    {
        _port = port;
        _stats = stats;
        _manager = new TupleSpaceManager();
        _broadcaster = broadcaster ?? new VisualizerBroadcaster();
    }

    public TupleSpaceManager Manager => _manager;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _stats.ConnectionStarted();
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var processId = $"P{Guid.NewGuid().ToString()[..8]}";
        try
        {
            using (client)
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var writerLock = new SemaphoreSlim(1, 1);
                var tasks = new List<Task>();
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) break;

                    var command = JsonSerializer.Deserialize<Command>(line);
                    if (command == null) continue;

                    var task = ProcessCommandAsync(command, writer, writerLock, processId, cancellationToken);
                    tasks.Add(task);

                    // Clean up completed tasks
                    tasks.RemoveAll(t => t.IsCompleted);
                }
                await Task.WhenAll(tasks);
            }
        }
        catch (Exception ex)
        {
            // Logging removed as we have a dashboard now, or could log to a dedicated window
        }
        finally
        {
            _stats.ConnectionEnded();
        }
    }

    private async Task ProcessCommandAsync(Command command, StreamWriter writer, SemaphoreSlim writerLock, string processId, CancellationToken cancellationToken)
    {
        var space = _manager.GetOrCreateSpace(command.SpaceName);
        object? response = null;

        switch (command.Type.ToUpperInvariant())
        {
            case "CREATE":
                response = new { Status = "OK" };
                break;

            case "OUT":
                if (command.Tuple != null)
                {
                    space.Add(command.Tuple);
                    _stats.IncrementOut();
                    _broadcaster.BroadcastEvent("OUT", command.SpaceName, processId, command.Tuple);
                    response = new { Status = "OK" };
                }
                break;

            case "OUTBULK":
                if (command.Tuples != null)
                {
                    space.AddBulk(command.Tuples);
                    _stats.IncrementOut(command.Tuples.Count);
                    _broadcaster.BroadcastEvent("OUTBULK", command.SpaceName, processId);
                    response = new { Status = "OK" };
                }
                break;

            case "IN":
                _broadcaster.BroadcastEvent("BLOCKED", command.SpaceName, processId, command.Tuple);
                var tuple = await space.GetAsync(command.Tuple, true, cancellationToken);
                _stats.IncrementIn();
                _broadcaster.BroadcastEvent("IN", command.SpaceName, processId, tuple);
                response = new { Status = "OK", Tuple = tuple };
                break;

            case "RD":
                _broadcaster.BroadcastEvent("BLOCKED", command.SpaceName, processId, command.Tuple);
                var rdTuple = await space.GetAsync(command.Tuple, false, cancellationToken);
                _stats.IncrementRd();
                _broadcaster.BroadcastEvent("RD", command.SpaceName, processId, rdTuple);
                response = new { Status = "OK", Tuple = rdTuple };
                break;

            case "INP":
                var inpTuple = space.TryGet(command.Tuple, true);
                _stats.IncrementInp();
                _broadcaster.BroadcastEvent("INP", command.SpaceName, processId, inpTuple);
                if (inpTuple != null)
                    response = new { Status = "OK", Tuple = inpTuple };
                else
                    response = new { Status = "Error", Message = "No matching tuple found" };
                break;

            case "RDP":
                var rdpTuple = space.TryGet(command.Tuple, false);
                _stats.IncrementRdp();
                _broadcaster.BroadcastEvent("RDP", command.SpaceName, processId, rdpTuple);
                if (rdpTuple != null)
                    response = new { Status = "OK", Tuple = rdpTuple };
                else
                    response = new { Status = "Error", Message = "No matching tuple found" };
                break;

            case "ISEMPTY":
                var isEmpty = space.IsEmpty();
                response = new { Status = "OK", IsEmpty = isEmpty };
                break;

            default:
                response = new { Status = "Error", Message = "Unknown command" };
                break;
        }

        if (response != null)
        {
            await writerLock.WaitAsync(cancellationToken);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            finally
            {
                writerLock.Release();
            }
        }
    }

    public class Command
    {
        public string Type { get; set; } = "";
        public string SpaceName { get; set; } = "";
        public string[]? Tuple { get; set; }
        public List<string[]>? Tuples { get; set; }
    }
}
