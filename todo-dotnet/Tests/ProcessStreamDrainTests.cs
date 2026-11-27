using System.Diagnostics;
using System.Text;

namespace Todo.Tests;

public sealed class ProcessStreamDrainTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string RegressionScript = Path.Combine(
        RepoRoot, "todo-dotnet", "scripts", "ProcessStreamDrain.Regression.ps1");

    [Fact]
    public async Task DrainContinuesAfterListenUrlSoChildCanExit()
    {
        Assert.True(File.Exists(RegressionScript), $"Missing {RegressionScript}");

        // The regression script uses ProcessStreamDrain.ps1 (same helper as verify.ps1).
        // After the listen URL, the child floods both pipes past capacity; if drains had
        // stopped at readiness the child would hang and this test would fail the wait.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var result = await RunPwshAsync(
            ["-NoProfile", "-File", RegressionScript],
            RepoRoot,
            cts.Token);

        Assert.True(
            result.ExitCode == 0,
            $"ProcessStreamDrain regression failed ({result.ExitCode}): {result.StdErr}\n{result.StdOut}");
        Assert.Contains("ProcessStreamDrain regression passed", result.StdOut, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPwshAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

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
            throw new InvalidOperationException("Failed to start pwsh.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
