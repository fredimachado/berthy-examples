#:package Aspire.Hosting.AppHost@17.0.0
#:package Aspire.Hosting.Dotnet@17.0.0
#:package Aspire.Hosting.JavaScript@17.0.0
#:package Aspire.Hosting.PostgreSQL@17.0.0
#:package Aspire.Hosting.Redis@17.0.0

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithImageTag("17");
var database = postgres.AddDatabase("tododb");
var redis = builder.AddRedis("redis").WithImageTag("7");

// Gate tests: HoldMigrateUntilStarted=true → migrate waits for an explicit start (ColdStart ordering).
var holdMigrate = string.Equals(
    builder.Configuration["HoldMigrateUntilStarted"],
    "true",
    StringComparison.OrdinalIgnoreCase);

var migrate = builder.AddDotnetProject(
        "migrate",
        "../Api/Todo.Api.csproj",
        options => options.ExcludeLaunchProfile = true)
    .WithArgs(context => context.Args.Add("migrate"))
    .WithReference(database)
    .WaitFor(database);

if (holdMigrate)
{
    migrate = migrate.WithExplicitStart();
}

var api = builder.AddDotnetProject("api", "../Api/Todo.Api.csproj")
    .WithReference(database)
    .WithReference(redis)
    .WaitForCompletion(migrate)
    .WaitFor(redis)
    .WithHttpHealthCheck("/health");

builder.AddViteApp("web", "../web")
    .WithReference(api)
    .WithEnvironment("API_HTTP", api.GetEndpoint("http"))
    .WithHttpHealthCheck("/")
    .WaitFor(api);

builder.Build().Run();
