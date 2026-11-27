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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var failed = new bool[probes.Count];
        var tasks = new Task[probes.Count];
        for (var i = 0; i < probes.Count; i++)
        {
            var index = i;
            var probe = probes[i];
            tasks[i] = ProbeAsync(probe, linked.Token, outcome => failed[index] = outcome);
        }

        var probeTask = Task.WhenAll(tasks);
        // One shared deadline via TimeProvider so a fake clock advances all probes together.
        // Also bounds drivers that ignore cancellation of an issued command.
        var deadlineTask = Task.Delay(Deadline, clock, ct);
        var completed = await Task.WhenAny(probeTask, deadlineTask);

        if (completed == deadlineTask)
        {
            await linked.CancelAsync();
            for (var i = 0; i < tasks.Length; i++)
            {
                if (!tasks[i].IsCompletedSuccessfully)
                {
                    failed[i] = true;
                }
            }

            // Keep driver work bounded for the caller; still observe later completions.
            _ = ObserveLaterAsync(tasks);
        }
        else
        {
            // ProbeAsync records failures itself; WhenAll should not fault.
            await probeTask;
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

    private static async Task ObserveLaterAsync(Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // Failures were already recorded for the response; ignore late faults.
        }
    }
}
