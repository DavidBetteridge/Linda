using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace TupleVisualizer;

public partial class MainWindow : Window
{
    private const int UdpPort = 9999;
    private readonly ConcurrentDictionary<string, GraphNode> _nodes = new();
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentQueue<VisualizerEvent> _eventQueue = new();
    private readonly ConcurrentDictionary<string, bool> _disconnectedProcesses = new();
    private readonly DispatcherTimer _renderTimer;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    
    private GraphNode? _draggedNode;
    private Point _dragOffset;
    
    public MainWindow()
    {
        InitializeComponent();
        
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50) // 20 FPS - throttle UI updates
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
        
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StartUdpListener();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _renderTimer.Stop();
        _cts?.Cancel();
        _udpClient?.Close();
    }

    private void StartUdpListener()
    {
        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient(UdpPort);
        
        Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    
                    try
                    {
                        var evt = JsonSerializer.Deserialize<VisualizerEvent>(json);
                        if (evt != null)
                        {
                            // Queue the event for processing - this handles rate limiting
                            // by only processing what we can in the render tick
                            _eventQueue.Enqueue(evt);
                            
                            // Limit queue size to prevent memory issues
                            while (_eventQueue.Count > 1000)
                            {
                                _eventQueue.TryDequeue(out _);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed packets
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (SocketException)
            {
                // Socket closed
            }
        });
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        // Process queued events (batch processing for rate limiting)
        var processedCount = 0;
        const int maxEventsPerTick = 100;
        
        while (processedCount < maxEventsPerTick && _eventQueue.TryDequeue(out var evt))
        {
            ProcessEvent(evt);
            processedCount++;
        }
        
        // Update the visual representation
        UpdateCanvas();
    }

    private void ProcessEvent(VisualizerEvent evt)
    {
        var processId = $"process:{evt.ProcessId}";
        
        // Handle process disconnection
        if (evt.EventType == "DISCONNECT")
        {
            _disconnectedProcesses[processId] = true;
            _nodes.TryRemove(processId, out _);
            // Remove all connections involving this process
            var connectionsToRemove = _connections.Keys.Where(k => k.StartsWith(processId)).ToList();
            foreach (var connId in connectionsToRemove)
            {
                _connections.TryRemove(connId, out _);
            }
            return;
        }
        
        // Ignore events for disconnected processes (may still be in queue)
        if (_disconnectedProcesses.ContainsKey(processId))
        {
            return;
        }
        
        // Ensure tuple space node exists
        var spaceId = $"space:{evt.SpaceName}";
        if (!_nodes.ContainsKey(spaceId))
        {
            var spaceNode = new GraphNode
            {
                Id = spaceId,
                Name = evt.SpaceName,
                NodeType = NodeType.TupleSpace,
                X = Random.Shared.Next(100, 800),
                Y = Random.Shared.Next(100, 500)
            };
            _nodes.TryAdd(spaceId, spaceNode);
        }
        
        // Ensure process node exists
        if (!_nodes.ContainsKey(processId))
        {
            var processNode = new GraphNode
            {
                Id = processId,
                Name = evt.ProcessId,
                NodeType = NodeType.Process,
                X = Random.Shared.Next(100, 800),
                Y = Random.Shared.Next(100, 500)
            };
            _nodes.TryAdd(processId, processNode);
        }
        
        // Update or create connection
        var connectionId = $"{processId}->{spaceId}";
        var connectionState = evt.EventType switch
        {
            "OUT" or "OUTBULK" => ConnectionState.Writing,
            "IN" or "RD" or "INP" or "RDP" => ConnectionState.Reading,
            "BLOCKED" => ConnectionState.Blocked,
            "UNBLOCKED" => ConnectionState.Reading,
            _ => ConnectionState.Reading
        };
        
        _connections[connectionId] = new Connection
        {
            Id = connectionId,
            FromNodeId = processId,
            ToNodeId = spaceId,
            State = connectionState,
            LastUpdate = DateTime.UtcNow
        };
    }

    private void UpdateCanvas()
    {
        GraphCanvas.Children.Clear();
        
        // Remove stale connections (older than 2 seconds)
        var staleThreshold = DateTime.UtcNow.AddSeconds(-2);
        var staleConnections = _connections.Where(c => c.Value.LastUpdate < staleThreshold).ToList();
        foreach (var stale in staleConnections)
        {
            _connections.TryRemove(stale.Key, out _);
        }
        
        // Draw connections first (so they appear behind nodes)
        foreach (var connection in _connections.Values)
        {
            if (_nodes.TryGetValue(connection.FromNodeId, out var fromNode) &&
                _nodes.TryGetValue(connection.ToNodeId, out var toNode))
            {
                DrawConnection(fromNode, toNode, connection.State);
            }
        }
        
        // Draw nodes
        foreach (var node in _nodes.Values)
        {
            DrawNode(node);
        }
    }

    private void DrawConnection(GraphNode from, GraphNode to, ConnectionState state)
    {
        var brush = state switch
        {
            ConnectionState.Writing => new SolidColorBrush(Color.Parse("#4CAF50")),
            ConnectionState.Reading => new SolidColorBrush(Color.Parse("#2196F3")),
            ConnectionState.Blocked => new SolidColorBrush(Color.Parse("#FF5722")),
            _ => new SolidColorBrush(Color.Parse("#888888"))
        };
        
        var line = new Line
        {
            StartPoint = new Point(from.X + 40, from.Y + 20),
            EndPoint = new Point(to.X + 40, to.Y + 20),
            Stroke = brush,
            StrokeThickness = state == ConnectionState.Blocked ? 3 : 2,
            StrokeDashArray = state == ConnectionState.Blocked ? new Avalonia.Collections.AvaloniaList<double> { 5, 3 } : null
        };
        
        GraphCanvas.Children.Add(line);
    }

    private void DrawNode(GraphNode node)
    {
        var nodeColor = node.NodeType == NodeType.TupleSpace
            ? Color.Parse("#4CAF50")
            : Color.Parse("#2196F3");
        
        var border = new Border
        {
            Width = 80,
            Height = 40,
            CornerRadius = new CornerRadius(node.NodeType == NodeType.TupleSpace ? 5 : 20),
            Background = new SolidColorBrush(nodeColor),
            BorderBrush = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(2),
            Child = new TextBlock
            {
                Text = node.Name.Length > 10 ? node.Name[..10] + "..." : node.Name,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Tag = node.Id
        };
        
        Canvas.SetLeft(border, node.X);
        Canvas.SetTop(border, node.Y);
        
        // Add drag handlers
        border.PointerPressed += OnNodePointerPressed;
        border.PointerMoved += OnNodePointerMoved;
        border.PointerReleased += OnNodePointerReleased;
        
        GraphCanvas.Children.Add(border);
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is string nodeId)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                _draggedNode = node;
                var position = e.GetPosition(GraphCanvas);
                _dragOffset = new Point(position.X - node.X, position.Y - node.Y);
                e.Pointer.Capture(border);
            }
        }
    }

    private void OnNodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedNode != null && sender is Border border)
        {
            var position = e.GetPosition(GraphCanvas);
            _draggedNode.X = position.X - _dragOffset.X;
            _draggedNode.Y = position.Y - _dragOffset.Y;
            
            // Clamp to canvas bounds
            _draggedNode.X = Math.Max(0, Math.Min(GraphCanvas.Bounds.Width - 80, _draggedNode.X));
            _draggedNode.Y = Math.Max(0, Math.Min(GraphCanvas.Bounds.Height - 40, _draggedNode.Y));
            
            UpdateCanvas();
        }
    }

    private void OnNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedNode != null && sender is Border border)
        {
            e.Pointer.Capture(null);
            _draggedNode = null;
        }
    }
}

public class GraphNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public NodeType NodeType { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

public enum NodeType
{
    TupleSpace,
    Process
}

public class Connection
{
    public string Id { get; set; } = "";
    public string FromNodeId { get; set; } = "";
    public string ToNodeId { get; set; } = "";
    public ConnectionState State { get; set; }
    public DateTime LastUpdate { get; set; }
}

public enum ConnectionState
{
    Reading,
    Writing,
    Blocked
}

public class VisualizerEvent
{
    public string EventType { get; set; } = "";
    public string SpaceName { get; set; } = "";
    public string ProcessId { get; set; } = "";
    public string[]? Tuple { get; set; }
}
