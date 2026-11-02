using System.Collections.Concurrent;

namespace TupleServer;

public class NodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();
    private readonly VisualizerBroadcaster? _broadcaster;

    public NodeRegistry(VisualizerBroadcaster? broadcaster = null)
    {
        _broadcaster = broadcaster;
    }

    public void RegisterNode(string nodeId, string version, string nodeType)
    {
        var nodeInfo = new NodeInfo
        {
            NodeId = nodeId,
            Version = version,
            NodeType = nodeType,
            LastHeartbeat = DateTime.UtcNow,
            ConnectedAt = DateTime.UtcNow
        };

        _nodes.AddOrUpdate(nodeId, nodeInfo, (_, existing) =>
        {
            existing.Version = version;
            existing.LastHeartbeat = DateTime.UtcNow;
            return existing;
        });

        BroadcastNodeList();
    }

    public void UpdateHeartbeat(string nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var nodeInfo))
        {
            nodeInfo.LastHeartbeat = DateTime.UtcNow;
        }
    }

    public void UnregisterNode(string nodeId)
    {
        _nodes.TryRemove(nodeId, out _);
        BroadcastNodeList();
    }

    public IEnumerable<NodeInfo> GetAllNodes()
    {
        return _nodes.Values.ToList();
    }

    private void BroadcastNodeList()
    {
        _broadcaster?.BroadcastNodeList(GetAllNodes().ToList());
    }
}

public class NodeInfo
{
    public string NodeId { get; set; } = "";
    public string Version { get; set; } = "";
    public string NodeType { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
}
