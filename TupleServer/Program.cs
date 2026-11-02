using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TupleServer;
using Spectre.Console;
using Spectre.Console.Rendering;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var noDashboard = args.Contains("--no-dashboard");

var stats = new ServerStatistics();
builder.Services.AddSingleton(stats);

var keysDir = Path.Combine(Directory.GetCurrentDirectory(), "keys");
var keysDirArgIndex = Array.IndexOf(args, "--keys-dir");
if (keysDirArgIndex != -1 && keysDirArgIndex + 1 < args.Length)
    keysDir = args[keysDirArgIndex + 1];
AnsiConsole.MarkupLine("[yellow]Keys Dir" + keysDir + "[/]");

if (!Directory.Exists(keysDir))
{
    Directory.CreateDirectory(keysDir);
}

// Load or generate server certificate
var certPath = Path.Combine(Directory.GetCurrentDirectory(), "server.pfx");
X509Certificate2 serverCert;
if (File.Exists(certPath))
{
    serverCert = X509CertificateLoader.LoadPkcs12FromFile(certPath, "password" );
}
else
{
    AnsiConsole.MarkupLine("[yellow]Generating self-signed server certificate...[/]");
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("cn=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    serverCert = request.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
    File.WriteAllBytes(certPath, serverCert.Export(X509ContentType.Pfx, "password"));
}

builder.Services.AddSingleton(serverCert);
var port = 8080;
var portArgIndex = Array.IndexOf(args, "--port");
if (portArgIndex != -1 && portArgIndex + 1 < args.Length)
    int.TryParse(args[portArgIndex + 1], out port);

var visualizerHost = "127.0.0.1";
var visualizerPort = 9999;
var vHostArgIndex = Array.IndexOf(args, "--visualizer-host");
if (vHostArgIndex != -1 && vHostArgIndex + 1 < args.Length)
    visualizerHost = args[vHostArgIndex + 1];

var vPortArgIndex = Array.IndexOf(args, "--visualizer-port");
if (vPortArgIndex != -1 && vPortArgIndex + 1 < args.Length)
    int.TryParse(args[vPortArgIndex + 1], out visualizerPort);

var broadcaster = new VisualizerBroadcaster(visualizerHost, visualizerPort);
builder.Services.AddSingleton(broadcaster);
builder.Services.AddSingleton(new TcpServer(port, stats, serverCert, broadcaster: broadcaster, keysDirectory: keysDir));

using var host = builder.Build();

var server = host.Services.GetRequiredService<TcpServer>();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var serverTask = server.StartAsync(cts.Token);

if (!noDashboard)
{
    await AnsiConsole.Live(CreateDashboard(stats, server.Manager))
        .StartAsync(async ctx =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                ctx.UpdateTarget(CreateDashboard(stats, server.Manager));
                try
                {
                    await Task.Delay(500, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
}

await serverTask;

await host.RunAsync();

static IRenderable CreateDashboard(ServerStatistics stats, TupleSpaceManager manager)
{
    var table = new Table().Centered();
    table.AddColumn("[yellow]Metric[/]");
    table.AddColumn("[yellow]Value[/]");

    table.AddRow("Active Connections", $"[green]{stats.ActiveConnections}[/]");
    table.AddRow("Total Spaces", $"[blue]{manager.SpaceCount}[/]");
    table.AddRow("Total OUT", stats.TotalOut.ToString());
    table.AddRow("Total IN", stats.TotalIn.ToString());
    table.AddRow("Total RD", stats.TotalRd.ToString());
    table.AddRow("Total INP", stats.TotalInp.ToString());
    table.AddRow("Total RDP", stats.TotalRdp.ToString());

    var spacesTable = new Table().Border(TableBorder.Rounded);
    spacesTable.AddColumn("Space Name");
    spacesTable.AddColumn("Tuples");
    spacesTable.AddColumn("Waiters");

    foreach (var (name, space) in manager.GetActiveSpaces().OrderBy(s => s.Name))
    {
        spacesTable.AddRow(name, space.Count.ToString(), space.WaiterCount.ToString());
    }

    return new Rows(
        new Rule("[red]Tuple Server Dashboard[/]"),
        table,
        new Rule("[blue]Active Spaces[/]"),
        spacesTable
    );
}
