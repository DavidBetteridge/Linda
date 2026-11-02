var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.TupleServer>("tupleserver")
    .WithArgs("--no-dashboard", 
              "--keys-dir", "/Users/davidbetteridge/tuples/TupleServer/keys",
              "--port", "9999",
              "--visualizer-host", "host.docker.internal")
    .WithEndpoint(port: 8080, name: "tcp", scheme: "tcp");

builder.AddProject<Projects.Addition>("addition")
    .WithArgs("/Users/davidbetteridge/tuples/SudokuSolver/key")
    .WithReference(server)
    .WithEnvironment("TUPLE_SERVER_HOST", server.GetEndpoint("tcp").Property(EndpointProperty.Host))
    .WithEnvironment("TUPLE_SERVER_PORT", server.GetEndpoint("tcp").Property(EndpointProperty.Port));

builder.AddProject<Projects.ExpressionRunner>("expressionrunner")
    .WithArgs("/Users/davidbetteridge/tuples/SudokuSolver/key", "client1")
    .WithReference(server)
    .WithEnvironment("TUPLE_SERVER_HOST", server.GetEndpoint("tcp").Property(EndpointProperty.Host))
    .WithEnvironment("TUPLE_SERVER_PORT", server.GetEndpoint("tcp").Property(EndpointProperty.Port));

builder.AddProject<Projects.SudokuSolver>("sudokusolver")
    .WithArgs("/Users/davidbetteridge/tuples/SudokuSolver/key")
    .WithReference(server)
    .WithEnvironment("TUPLE_SERVER_HOST", server.GetEndpoint("tcp").Property(EndpointProperty.Host))
    .WithEnvironment("TUPLE_SERVER_PORT", server.GetEndpoint("tcp").Property(EndpointProperty.Port));

builder.Build().Run();
