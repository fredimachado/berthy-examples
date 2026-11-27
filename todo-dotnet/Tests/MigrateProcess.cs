using System.Diagnostics;
using System.Text;

namespace Todo.Tests;

internal static class MigrateProcess
{
    private static readonly string ApiDll = Path.Combine(AppContext.BaseDirectory, "Todo.Api.dll");

    internal static async Task<MigrateResult> RunAsync(
        string tododbConnectionString,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ApiDll))
        {
            throw new InvalidOperationException(
                $"Prebuilt migrator binary not found at '{ApiDll}'. Build Todo.Tests before running migrate.");
        }

        // Run the already-built DLL so concurrent migrators never contend on CSC writing Todo.Api.dll.
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { ApiDll, "migrate" },
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["ConnectionStrings__tododb"] = tododbConnectionString;
        // Keep ASP.NET from binding an HTTP port in migrate mode if startup races.
        start.Environment["ASPNETCORE_URLS"] = "";

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start migrate process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new MigrateResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

internal sealed record MigrateResult(int ExitCode, string StdOut, string StdErr);
