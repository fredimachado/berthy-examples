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
        // Start the shared deadline before any probe work so cold connects cannot steal budget.
        var deadlineTask = Task.Delay(Deadline, clock, ct);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outcomes = new int[probes.Count];
        var tasks = new Task[probes.Count];
        for (var i = 0; i < probes.Count; i++)
        {
            var index = i;
            var probe = probes[i];
            // Probes run independently of the response wait; only the deadline gates the reply.
            tasks[i] = ProbeAsync(probe, linked.Token, outcomes, index);
        }

        var probeTask = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(probeTask, deadlineTask);

        if (completed == deadlineTask)
        {
            // Pending → failed. Late success cannot overwrite a timeout (CAS from Pending only).
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
        int index)
    {
        try
        {
            await probe.CheckAsync(ct);
            Interlocked.CompareExchange(ref outcomes[index], SucceededInBudget, Pending);
        }
        catch (Exception)
        {
            Interlocked.CompareExchange(ref outcomes[index], Failed, Pending);
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
