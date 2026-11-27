internal sealed record DependencyProbe(
    string Name, Func<CancellationToken, Task> CheckAsync);

internal static class DependencyHealth
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    private const int Pending = 0;
    private const int SucceededInBudget = 1;
    private const int Failed = 2;

    /// <summary>
    /// Runs probes concurrently under one shared deadline. Failures keep probe order.
    /// </summary>
    internal static async Task<string[]> FailedDependenciesAsync(
        IReadOnlyList<DependencyProbe> probes, TimeProvider clock, CancellationToken ct)
    {
        // Shared start for elapsed checks at success-record time (CAS alone is not enough).
        var startedAt = clock.GetTimestamp();
        var deadlineTask = Task.Delay(Deadline, clock, ct);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outcomes = new int[probes.Count];
        var tasks = new Task[probes.Count];
        for (var i = 0; i < probes.Count; i++)
        {
            var index = i;
            var probe = probes[i];
            // Schedule on the thread pool so sync work before a probe's first await cannot
            // delay starting sibling probes or arming Task.WhenAny on the deadline.
            tasks[i] = Task.Run(
                () => ProbeAsync(probe, linked.Token, outcomes, index, clock, startedAt),
                CancellationToken.None);
        }

        var probeTask = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(probeTask, deadlineTask);

        if (completed == deadlineTask)
        {
            for (var i = 0; i < outcomes.Length; i++)
            {
                Interlocked.CompareExchange(ref outcomes[i], Failed, Pending);
            }

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
            if (Volatile.Read(ref outcomes[i]) != SucceededInBudget)
            {
                names.Add(probes[i].Name);
            }
        }

        return names.ToArray();
    }

    private static async Task ProbeAsync(
        DependencyProbe probe,
        CancellationToken ct,
        int[] outcomes,
        int index,
        TimeProvider clock,
        long startedAt)
    {
        try
        {
            await probe.CheckAsync(ct);
            TryRecordSuccess(outcomes, index, clock, startedAt);
        }
        catch (Exception)
        {
            Interlocked.CompareExchange(ref outcomes[index], Failed, Pending);
        }
    }

    /// <summary>
    /// Success counts only when recorded before the shared deadline elapses.
    /// </summary>
    private static void TryRecordSuccess(
        int[] outcomes, int index, TimeProvider clock, long startedAt)
    {
        if (clock.GetElapsedTime(startedAt) >= Deadline)
        {
            Interlocked.CompareExchange(ref outcomes[index], Failed, Pending);
            return;
        }

        Interlocked.CompareExchange(ref outcomes[index], SucceededInBudget, Pending);
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
