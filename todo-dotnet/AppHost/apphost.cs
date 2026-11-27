#:package Aspire.Hosting.AppHost@17.0.0
#:package Aspire.Hosting.Dotnet@17.0.0
#:package Aspire.Hosting.PostgreSQL@17.0.0
#:package Aspire.Hosting.Redis@17.0.0

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithImageTag("17");
var database = postgres.AddDatabase("tododb");
var redis = builder.AddRedis("redis").WithImageTag("7");

// Gate tests set PlantMigrationConflict=true so a one-shot conflict resource runs before migrate.
var plantConflict = string.Equals(
    builder.Configuration["PlantMigrationConflict"],
    "true",
    StringComparison.OrdinalIgnoreCase);

var migrate = builder.AddDotnetProject(
        "migrate",
        "../Api/Todo.Api.csproj",
        options => options.ExcludeLaunchProfile = true)
    .WithArgs(context => context.Args.Add("migrate"))
    .WithReference(database);

if (plantConflict)
{
    var conflict = builder.AddDotnetProject(
            "conflict",
            "../Api/Todo.Api.csproj",
            options => options.ExcludeLaunchProfile = true)
        .WithArgs(context => context.Args.Add("plant-migration-conflict"))
        .WithReference(database)
        .WaitFor(database);

    migrate = migrate.WaitForCompletion(conflict);
}
else
{
    migrate = migrate.WaitFor(database);
}

builder.AddDotnetProject("api", "../Api/Todo.Api.csproj")
    .WithReference(database)
    .WithReference(redis)
    .WaitForCompletion(migrate)
    .WaitFor(redis)
    .WithHttpHealthCheck("/version");

builder.Build().Run();
