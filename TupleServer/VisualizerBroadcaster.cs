using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TupleServer;

public class VisualizerBroadcaster(string host = "127.0.0.1", int port = 9999, bool enabled = true)
    : IDisposable
{
    private readonly UdpClient _udpClient = new();
    private readonly IPEndPoint _endpoint = new(IPAddress.Parse(host), port);

    public void BroadcastEvent(string eventType, string spaceName, string processId, string[]? tuple = null)
    {
        if (!enabled) return;
        
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
            
            // Fire and forget - don't wait for send to complete
            // UDP is connectionless so this is fast
            _udpClient.SendAsync(bytes, bytes.Length, _endpoint);
        }
        catch
        {
            // Ignore broadcast failures - visualization is optional
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
