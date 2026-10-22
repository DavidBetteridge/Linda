using TupleServer;
using Spectre.Console;
using Spectre.Console.Rendering;

var cts = new CancellationTokenSource();
var stats = new ServerStatistics();
var keysDir = Path.Combine(Directory.GetCurrentDirectory(), "keys");
if (!Directory.Exists(keysDir))
{
    AnsiConsole.MarkupLine("[red]Keys directory does not exist.[/]");
    return;
}
AnsiConsole.MarkupLine("[green]Keys directory: " + keysDir + "[/]");

var server = new TcpServer(8080, stats, keysDirectory: keysDir);

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var serverTask = server.StartAsync(cts.Token);

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

await serverTask;

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
