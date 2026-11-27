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
    /// <param name="onSucceededInBudget">
    /// Optional hook invoked after a probe is recorded as in-budget success (for tests).
    /// </param>
    internal static async Task<string[]> FailedDependenciesAsync(
        IReadOnlyList<DependencyProbe> probes,
        TimeProvider clock,
        CancellationToken ct,
        Action<string>? onSucceededInBudget = null)
    {
        // Shared start for elapsed checks at success-record time (CAS alone is not enough).
        var startedAt = clock.GetTimestamp();
        var deadlineTask = Task.Delay(Deadline, clock, ct);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Capture token before scheduling; workers must not read a cleared CTS local.
        var token = linked.Token;
        var outcomes = new int[probes.Count];
        var tasks = new Task[probes.Count];
        for (var i = 0; i < probes.Count; i++)
        {
            var index = i;
            var probe = probes[i];
            // Schedule on the thread pool so sync work before a probe's first await cannot
            // delay starting sibling probes or arming Task.WhenAny on the deadline.
            tasks[i] = Task.Run(
                () => ProbeAsync(probe, token, outcomes, index, clock, startedAt, onSucceededInBudget),
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

            _ = CleanupAfterDeadlineAsync(linked, tasks);
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
        long startedAt,
        Action<string>? onSucceededInBudget)
    {
        try
        {
            await probe.CheckAsync(ct);
            if (TryRecordSuccess(outcomes, index, clock, startedAt))
            {
                onSucceededInBudget?.Invoke(probe.Name);
            }
        }
        catch (Exception)
        {
            Interlocked.CompareExchange(ref outcomes[index], Failed, Pending);
        }
    }

    /// <summary>
    /// Success counts only when recorded before the shared deadline elapses.
    /// Returns true when this call recorded an in-budget success.
    /// </summary>
    private static bool TryRecordSuccess(
        int[] outcomes, int index, TimeProvider clock, long startedAt)
    {
        if (clock.GetElapsedTime(startedAt) >= Deadline)
        {
            Interlocked.CompareExchange(ref outcomes[index], Failed, Pending);
            return false;
        }

        return Interlocked.CompareExchange(ref outcomes[index], SucceededInBudget, Pending) == Pending;
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
