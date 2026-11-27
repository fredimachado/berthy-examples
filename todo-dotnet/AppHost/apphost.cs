#:package Aspire.Hosting.AppHost@17.0.0
#:package Aspire.Hosting.Dotnet@17.0.0

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDotnetProject("api", "../Api/Todo.Api.csproj")
    .WithHttpHealthCheck("/version");

builder.Build().Run();
