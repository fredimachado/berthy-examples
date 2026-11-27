internal sealed record DependencyProbe(
    string Name, Func<CancellationToken, Task> CheckAsync);

internal static class DependencyHealth
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs probes concurrently under one shared deadline. Failures keep probe order.
    /// </summary>
    internal static async Task<string[]> FailedDependenciesAsync(
        IReadOnlyList<DependencyProbe> probes, TimeProvider clock, CancellationToken ct)
    {
        // Start the shared deadline before any probe work so cold connects cannot steal budget.
        var deadlineTask = Task.Delay(Deadline, clock, ct);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var failed = new bool[probes.Count];
        var tasks = new Task[probes.Count];
        for (var i = 0; i < probes.Count; i++)
        {
            var index = i;
            var probe = probes[i];
            tasks[i] = ProbeAsync(probe, linked.Token, outcome => failed[index] = outcome);
        }

        var probeTask = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(probeTask, deadlineTask);

        if (completed == deadlineTask)
        {
            // Capture unfinished probes before any cancellation work.
            for (var i = 0; i < tasks.Length; i++)
            {
                if (!tasks[i].IsCompletedSuccessfully)
                {
                    failed[i] = true;
                }
            }

            // Cancel and dispose off the response path — CancelAsync waits for callbacks.
            var toCleanup = linked;
            linked = null!;
            _ = CleanupAfterDeadlineAsync(toCleanup, tasks);
        }
        else
        {
            try
            {
                await probeTask;
            }
            finally
            {
                linked.Dispose();
            }
        }

        var names = new List<string>(probes.Count);
        for (var i = 0; i < probes.Count; i++)
        {
            if (failed[i])
            {
                names.Add(probes[i].Name);
            }
        }

        return names.ToArray();
    }

    private static async Task ProbeAsync(
        DependencyProbe probe, CancellationToken ct, Action<bool> setFailed)
    {
        try
        {
            await probe.CheckAsync(ct);
        }
        catch (Exception)
        {
            setFailed(true);
        }
    }

    private static Task CleanupAfterDeadlineAsync(CancellationTokenSource cts, Task[] tasks) =>
        Task.Run(async () =>
        {
            try
            {
                // Runs on the thread pool so a held cancel callback cannot stall the HTTP response.
                await cts.CancelAsync();
            }
            catch (Exception)
            {
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
            }
            finally
            {
                cts.Dispose();
            }
        });
}
