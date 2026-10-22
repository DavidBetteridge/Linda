using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TupleClient;


if (args.Length == 0)
{
    Console.WriteLine("Please supply the name of the client.");
    return;
}

var keysDir = Path.Combine(Directory.GetCurrentDirectory(), "key");
if (!Directory.Exists(keysDir))
{
    Console.WriteLine("Key directory does not exist");
    return;
}

var keys = Directory.GetFiles(keysDir, "*.pem");
if (keys.Length != 1)
{
    Console.WriteLine("Key directory must contact EXACTLY ONE pem file");
    return;
}

var keyId = Path.GetFileNameWithoutExtension(keys[0]);
var privateKey = await File.ReadAllTextAsync(keys[0]);

var processName = args[0];



Console.WriteLine($"Expression Runner {processName} started...");

using var expressionClient = new TupleSpaceClient("127.0.0.1", 8080, "expressions", keyId, privateKey);

// Set up script options with necessary references and imports
var scriptOptions = ScriptOptions.Default
    .AddReferences(typeof(TupleSpaceClient).Assembly)
    .AddImports("System", "System.Threading.Tasks", "TupleClient", "System.Collections.Generic");

while (true)
{
    try
    {
        // Tuple format: ["expression", spaceName, code, tupleDefinitions, data]
        var tuple = await expressionClient.InAsync("expression", "*", "*", "*", "*");
        var spaceName = tuple[1];
        var code = tuple[2];
        var tupleDefinitions = tuple[3];
        var workerId = int.Parse(tuple[4]);

        Console.WriteLine($"Received code for space: {spaceName}");

        _ = Task.Run(async () =>
        {
            using var client = new TupleSpaceClient("127.0.0.1", 8080, spaceName, keyId, privateKey);
            try
            {
                Console.WriteLine($"Starting execution for space: {spaceName}");
                
                // Prepend tuple definitions to the code if provided
                var fullCode = string.IsNullOrEmpty(tupleDefinitions) 
                    ? code 
                    : tupleDefinitions + "\n\n" + code;
                
                // Create a script that has access to the client via a global variable 'c'
                var script = CSharpScript.Create<Task>(
                    fullCode,
                    scriptOptions,
                    globalsType: typeof(ScriptGlobals));
                
                var compiled = script.Compile();
                if (compiled.Length > 0)
                {
                    Console.WriteLine($"Compilation errors for space {spaceName}:");
                    foreach (var diagnostic in compiled)
                    {
                        Console.WriteLine($"  {diagnostic}");
                    }
                    return;
                }
                
                var globals = new ScriptGlobals { c = client, processName = processName, workerId = workerId};
                var result = await script.RunAsync(globals);
                
                // If the script returns a Task, await it
                if (result.ReturnValue is { } task)
                {
                    await task;
                }
                
                Console.WriteLine($"Completed execution for space: {spaceName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing code for space {spaceName}: {ex}");
            }
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in Expression Runner loop: {ex.Message}");
        await Task.Delay(1000);
    }
}

public class ScriptGlobals
{
    public TupleSpaceClient c { get; set; } = null!;
    public required string processName { get; set; }
    public required int workerId { get; set; }
}
