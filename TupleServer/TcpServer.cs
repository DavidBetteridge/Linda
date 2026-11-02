using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using OpenTelemetry.Trace;
using TupleServer.Auth;

namespace TupleServer;

public class TcpServer
{
    public static readonly ActivitySource ActivitySource = new("TupleServer");
    private readonly int _port;
    private readonly TupleSpaceManager _manager;
    private readonly ServerStatistics _stats;
    private readonly VisualizerBroadcaster _broadcaster;
    private readonly KeyManager? _keyManager;
    private readonly X509Certificate2 _serverCertificate;
    private readonly NodeRegistry _nodeRegistry;

    public TcpServer(int port, ServerStatistics stats, X509Certificate2 serverCertificate, VisualizerBroadcaster? broadcaster = null, string? keysDirectory = null)
    {
        _port = port;
        _stats = stats;
        _manager = new TupleSpaceManager();
        _broadcaster = broadcaster ?? new VisualizerBroadcaster();
        _serverCertificate = serverCertificate ?? throw new ArgumentNullException(nameof(serverCertificate));
        _nodeRegistry = new NodeRegistry(_broadcaster);
        if (keysDirectory != null)
        {
            _keyManager = new KeyManager(keysDirectory);
        }
    }

    public TupleSpaceManager Manager => _manager;
    public NodeRegistry NodeRegistry => _nodeRegistry;

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
        Console.WriteLine("Client connecting");
        
        using var activity = ActivitySource.StartActivity("Connection");
        var processId = $"P{Guid.NewGuid().ToString()[..8]}";
        activity?.SetTag("process.id", processId);
        try
        {
            using (client)
            {
                Stream stream = client.GetStream();
                var sslStream = new SslStream(stream, false);
                await sslStream.AuthenticateAsServerAsync(_serverCertificate, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                stream = sslStream;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var writerLock = new SemaphoreSlim(1, 1);
                var tasks = new List<Task>();

                if (_keyManager != null)
                {
                    if (!await AuthenticateClientAsync(reader, writer, writerLock, cancellationToken))
                    {
                        return;
                    }
                }

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
        catch (Exception)
        {
            // Logging removed as we have a dashboard now, or could log to a dedicated window
        }
        finally
        {
            _broadcaster.BroadcastEvent("DISCONNECT", "", processId);
            _stats.ConnectionEnded();
        }
    }

    private async Task<bool> AuthenticateClientAsync(StreamReader reader, StreamWriter writer, SemaphoreSlim writerLock, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("Authenticate");
        try
        {
            // 1. Wait for Auth command
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) return false;

            var authCommand = JsonSerializer.Deserialize<Command>(line);
            if (authCommand == null || authCommand.Type != "AUTH")
            {
                await SendErrorAsync(writer, writerLock, "Authentication required", cancellationToken);
                return false;
            }

            var keyId = authCommand.KeyId;
            if (string.IsNullOrEmpty(keyId) || _keyManager?.GetPublicKey(keyId) == null)
            {
                await SendErrorAsync(writer, writerLock, "Invalid Key ID", cancellationToken);
                return false;
            }

            // 2. Send Challenge
            var challenge = RandomNumberGenerator.GetBytes(32);
            var challengeBase64 = Convert.ToBase64String(challenge);
            await SendResponseAsync(writer, writerLock, new { Status = "CHALLENGE", Challenge = challengeBase64 }, cancellationToken);

            // 3. Wait for Signature
            line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) return false;

            var sigCommand = JsonSerializer.Deserialize<Command>(line);
            if (sigCommand == null || sigCommand.Type != "RESPONSE")
            {
                await SendErrorAsync(writer, writerLock, "Expected RESPONSE", cancellationToken);
                return false;
            }

            var signature = Convert.FromBase64String(sigCommand.Signature ?? "");
            if (_keyManager.VerifyResponse(keyId, challenge, signature))
            {
                await SendResponseAsync(writer, writerLock, new { Status = "OK" }, cancellationToken);
                return true;
            }
            else
            {
                await SendErrorAsync(writer, writerLock, "Invalid signature", cancellationToken);
                return false;
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.message", ex.Message);
            activity?.SetTag("exception.stacktrace", ex.ToString());
            // Logging can be added here if needed for debugging
            return false;
        }
    }

    private async Task SendErrorAsync(StreamWriter writer, SemaphoreSlim writerLock, string message, CancellationToken cancellationToken)
    {
        await SendResponseAsync(writer, writerLock, new { Status = "Error", Message = message }, cancellationToken);
    }

    private async Task SendResponseAsync(StreamWriter writer, SemaphoreSlim writerLock, object response, CancellationToken cancellationToken)
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

    private async Task ProcessCommandAsync(Command command, StreamWriter writer, SemaphoreSlim writerLock, string processId, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("ProcessCommand");
        activity?.SetTag("command.type", command.Type);
        activity?.SetTag("space.name", command.SpaceName);
        activity?.SetTag("process.id", processId);

        try
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
                        activity?.SetTag("tuple.size", command.Tuple.Length);
                        space.Add(command.Tuple);
                        _stats.IncrementOut();
                        _broadcaster.BroadcastEvent("OUT", command.SpaceName, processId, command.Tuple);
                        response = new { Status = "OK" };
                    }
                    break;

                case "OUTBULK":
                    if (command.Tuples != null)
                    {
                        activity?.SetTag("tuples.count", command.Tuples.Count);
                        space.AddBulk(command.Tuples);
                        _stats.IncrementOut(command.Tuples.Count);
                        _broadcaster.BroadcastEvent("OUTBULK", command.SpaceName, processId);
                        response = new { Status = "OK" };
                    }
                    break;

                case "IN":
                    activity?.AddEvent(new ActivityEvent("BlockedForIn"));
                    _broadcaster.BroadcastEvent("BLOCKED", command.SpaceName, processId, command.Tuple);
                    var tuple = await space.GetAsync(command.Tuple, true, cancellationToken);
                    _stats.IncrementIn();
                    _broadcaster.BroadcastEvent("IN", command.SpaceName, processId, tuple);
                    response = new { Status = "OK", Tuple = tuple };
                    break;

                case "RD":
                    activity?.AddEvent(new ActivityEvent("BlockedForRd"));
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
                
                case "UPLOAD_KEY":
                    if (_keyManager != null && !string.IsNullOrEmpty(command.KeyId) && !string.IsNullOrEmpty(command.PublicKey))
                    {
                        _keyManager.AddPublicKey(command.KeyId, command.PublicKey);
                        response = new { Status = "OK" };
                    }
                    else
                    {
                        response = new { Status = "Error", Message = "Invalid key data" };
                    }
                    break;

                case "REVOKE_KEY":
                    if (_keyManager != null && !string.IsNullOrEmpty(command.KeyId))
                    {
                        _keyManager.RevokePublicKey(command.KeyId);
                        response = new { Status = "OK" };
                    }
                    else
                    {
                        response = new { Status = "Error", Message = "Invalid Key ID" };
                    }
                    break;

                case "REGISTER_NODE":
                    if (!string.IsNullOrEmpty(command.NodeId) && !string.IsNullOrEmpty(command.Version))
                    {
                        _nodeRegistry.RegisterNode(command.NodeId, command.Version, command.NodeType ?? "Unknown");
                        response = new { Status = "OK" };
                    }
                    else
                    {
                        response = new { Status = "Error", Message = "Invalid node registration data" };
                    }
                    break;

                case "GET_NODES":
                    var nodes = _nodeRegistry.GetAllNodes().Select(n => new 
                    { 
                        n.NodeId, 
                        n.Version, 
                        n.NodeType, 
                        ConnectedAt = n.ConnectedAt.ToString("O"),
                        LastHeartbeat = n.LastHeartbeat.ToString("O")
                    }).ToList();
                    response = new { Status = "OK", Nodes = nodes };
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
        catch (OperationCanceledException)
        {
            // Expected when client disconnects or server shuts down
        }
        catch (Exception)
        {
            // Handle other potential errors during command processing
        }
    }

    public class Command
    {
        public string Type { get; set; } = "";
        public string SpaceName { get; set; } = "";
        public string[]? Tuple { get; set; }
        public List<string[]>? Tuples { get; set; }
        public string? KeyId { get; set; }
        public string? PublicKey { get; set; }
        public string? Signature { get; set; }
        public string? NodeId { get; set; }
        public string? Version { get; set; }
        public string? NodeType { get; set; }
    }
}
