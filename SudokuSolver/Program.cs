using TupleClient;

// A Sudoku grid is represented as a string of 81 characters, where ' ' or '0' or '.' is an empty square.
// We'll use a string for simplicity in the tuple.
const string initialGrid = "53  7    6  195    98    6 8   6   34  8 3  17   2   6 6    28    419  5    8  79";

var spaceName = "sudoku-" + Guid.NewGuid();
var keyId = Environment.GetEnvironmentVariable("TUPLE_KEY_ID");
var privateKey = Environment.GetEnvironmentVariable("TUPLE_PRIVATE_KEY");
using var client = new TupleSpaceClient("127.0.0.1", 8080, spaceName, keyId, privateKey);

Console.WriteLine("Solving Sudoku...");
Console.WriteLine(FormatGrid(initialGrid));

await client.OutAsync(new GridTuple { Grid = NormalizeGrid(initialGrid) });

var workerCount = 10;
var workers = new Task[workerCount];
for (var i = 0; i < workerCount; i++)
{
    await client.OutAsync(new AliveTuple { WorkerId = i } );
    workers[i] = client.RunRemotelyAsync(RemoteCode.PerformSudokuSolve, RemoteCode.TupleDefinitions, i);
}

// Wait for a solution
var solution = await client.InAsync<SolutionTuple>(Wildcard.Any);
Console.WriteLine("Solution Found:");
Console.WriteLine(FormatGrid(solution.Grid));

// Kill all the workers
for (var i = 0; i < workerCount; i++)
    await client.InAsync<AliveTuple>(i);

string NormalizeGrid(string grid) => grid.Replace('.', ' ').Replace('0', ' ');

string FormatGrid(string grid)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 9; i++)
    {
        if (i % 3 == 0 && i != 0) sb.AppendLine("------+-------+------");
        for (int j = 0; j < 9; j++)
        {
            if (j % 3 == 0 && j != 0) sb.Append("| ");
            char c = grid[i * 9 + j];
            sb.Append(c == ' ' ? ". " : c + " ");
        }
        sb.AppendLine();
    }
    return sb.ToString();
}

[TupleDefinition]
public readonly struct GridTuple
{
    public required string Grid { get; init; }
}

[TupleDefinition]
public readonly struct SolutionTuple
{
    public required string Grid { get; init; }
}

[TupleDefinition]
public readonly struct AliveTuple
{
    public required int WorkerId { get; init; }
}

public static class SudokuLogic
{
    [RemoteEval]
    public static async Task PerformSudokuSolve(TupleSpaceClient c, string processName, int workerId)
    {
        while (true)
        {
            // 1. Check we are still alive
            var alive = await c.RdpAsync<AliveTuple>(workerId);
            if (alive is null) break;

            // 2. Get a ('Grid', ?) tuple from the tuple space.
            var gridTuple = await c.InpAsync<GridTuple>(Wildcard.Any);
            if (gridTuple is null)
            {
                // No more grids to process currently. 
                // In a distributed system, we might want to wait a bit or exit if we know no more work will be produced.
                // For this example, we'll wait a short time and try again, or exit if we're done.
                await Task.Delay(100);
                continue;
            }

            var grid = gridTuple.Value.Grid;

            // 3. Find the first empty square in the grid.
            int emptyIndex = grid.IndexOf(' ');

            // 4. If no empty square exists, then this is the solution. Write a ("Solution", completed_grid) tuple and exit.
            if (emptyIndex == -1)
            {
                await c.OutAsync(new SolutionTuple { Grid = grid });
                break;
            }

            // 5. Work out all the possible values for the empty square.
            var possibleValues = GetPossibleValues(grid, emptyIndex);

            // 6. For each possible value, write a new (Grid, ) tuple, but with square in the grid with the possible value added.
            using var scope = new BulkOutScope(c);
            foreach (var val in possibleValues)
            {
                var newGrid = grid.ToCharArray();
                newGrid[emptyIndex] = val;
                await c.OutAsync(new GridTuple { Grid = new string(newGrid) });
            }

            // 7. Loop back to 1
        }


        static List<char> GetPossibleValues(string grid, int index)
        {
            var result = new List<char>();
            int row = index / 9;
            int col = index % 9;

            for (char val = '1'; val <= '9'; val++)
            {
                if (IsPossible(grid, row, col, val))
                {
                    result.Add(val);
                }
            }

            return result;
        }

        static bool IsPossible(string grid, int row, int col, char val)
        {
            // Check row
            for (int i = 0; i < 9; i++)
            {
                if (grid[row * 9 + i] == val) return false;
            }

            // Check column
            for (int i = 0; i < 9; i++)
            {
                if (grid[i * 9 + col] == val) return false;
            }

            // Check 3x3 box
            int boxRow = (row / 3) * 3;
            int boxCol = (col / 3) * 3;
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    if (grid[(boxRow + i) * 9 + (boxCol + j)] == val) return false;
                }
            }

            return true;
        }
    }
}
