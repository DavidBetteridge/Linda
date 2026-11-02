using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TupleServer;

public class VisualizerBroadcaster : IDisposable
{
    private readonly UdpClient _udpClient = new();
    private readonly string _host;
    private readonly int _port;
    private readonly bool _enabled;

    public VisualizerBroadcaster(string host = "127.0.0.1", int port = 9999, bool enabled = true)
    {
        _host = host;
        _port = port;
        _enabled = enabled;
    }

    public void BroadcastEvent(string eventType, string spaceName, string processId, string[]? tuple = null)
    {
        if (!_enabled) return;
        
        try
        {
            var evt = new VisualizerEvent
            {
                EventType = eventType,
                SpaceName = spaceName,
                ProcessId = processId,
                Tuple = tuple
            };
            
            var json = JsonSerializer.Serialize(evt);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            // Resolve host to IP address
            // Note: In a production environment, you might want to cache this or use a more robust resolution mechanism.
            // For now, we resolve it each time to handle dynamic environments.
            var addresses = Dns.GetHostAddresses(_host);
            if (addresses.Length == 0) return;
            
            var endpoint = new IPEndPoint(addresses[0], _port);
            
            // Fire and forget - don't wait for send to complete
            // UDP is connectionless so this is fast
            _udpClient.SendAsync(bytes, bytes.Length, endpoint);
        }
        catch
        {
            // Ignore broadcast failures - visualization is optional
        }
    }

    public void BroadcastNodeList(List<NodeInfo> nodes)
    {
        if (!_enabled) return;
        
        try
        {
            var evt = new NodeListEvent
            {
                EventType = "NODE_LIST",
                Nodes = nodes
            };
            
            var json = JsonSerializer.Serialize(evt);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            var addresses = Dns.GetHostAddresses(_host);
            if (addresses.Length == 0) return;
            
            var endpoint = new IPEndPoint(addresses[0], _port);
            _udpClient.SendAsync(bytes, bytes.Length, endpoint);
        }
        catch
        {
            // Ignore broadcast failures - cluster management is optional
        }
    }
    
    public void Dispose()
    {
        _udpClient.Dispose();
    }
}

public class VisualizerEvent
{
    public string EventType { get; set; } = "";
    public string SpaceName { get; set; } = "";
    public string ProcessId { get; set; } = "";
    public string[]? Tuple { get; set; }
}

public class NodeListEvent
{
    public string EventType { get; set; } = "";
    public List<NodeInfo> Nodes { get; set; } = new();
}
