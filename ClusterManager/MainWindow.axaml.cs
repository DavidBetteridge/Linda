using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace ClusterManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<NodeViewModel> _nodes = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private DataGrid _nodesGrid = null!;
    private TextBlock _statusText = null!;

    public MainWindow()
    {
        InitializeComponent();
        
        _nodesGrid = this.FindControl<DataGrid>("NodesGrid")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _nodesGrid.ItemsSource = _nodes;
        
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StartUdpListener();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        _udpClient?.Dispose();
    }

    private void StartUdpListener()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            try
            {
                _udpClient = new UdpClient(9999);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText.Text = "Listening on UDP port 9999...";
                });

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _udpClient.ReceiveAsync(token);
                        var json = Encoding.UTF8.GetString(result.Buffer);
                        
                        var evt = JsonSerializer.Deserialize<NodeListEvent>(json);
                        if (evt?.EventType == "NODE_LIST" && evt.Nodes != null)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                UpdateNodeList(evt.Nodes);
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            StatusText.Text = $"Error: {ex.Message}";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText.Text = $"Failed to start UDP listener: {ex.Message}";
                });
            }
        }, token);
    }

    private void UpdateNodeList(List<NodeInfo> nodes)
    {
        _nodes.Clear();
        foreach (var node in nodes)
        {
            _nodes.Add(new NodeViewModel
            {
                NodeId = node.NodeId,
                NodeType = node.NodeType,
                Version = node.Version,
                ConnectedAt = node.ConnectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                LastHeartbeat = node.LastHeartbeat.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        StatusText.Text = $"Showing {nodes.Count} node(s) - Last updated: {DateTime.Now:HH:mm:ss}";
    }
}

public class NodeViewModel
{
    public string NodeId { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string Version { get; set; } = "";
    public string ConnectedAt { get; set; } = "";
    public string LastHeartbeat { get; set; } = "";
}

public class NodeListEvent
{
    public string EventType { get; set; } = "";
    public List<NodeInfo>? Nodes { get; set; }
}

public class NodeInfo
{
    public string NodeId { get; set; } = "";
    public string Version { get; set; } = "";
    public string NodeType { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
}
