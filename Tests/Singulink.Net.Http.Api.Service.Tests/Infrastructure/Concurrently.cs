using System.Runtime.ExceptionServices;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Runs an action on several dedicated threads at the same moment, for a number of rounds, to exercise races.
/// </summary>
public static class Concurrently
{
    /// <summary>
    /// Runs an asynchronous <paramref name="action"/> on <paramref name="threads"/> threads in each of <paramref name="rounds"/> rounds, waiting for each
    /// call to finish. All threads start a round together. Returns the results of each round.
    /// </summary>
    public static T[][] RunTasks<T>(int threads, int rounds, Func<int, Task<T>> action)
    {
        return Run(threads, rounds, round => action(round).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Runs <paramref name="action"/> on <paramref name="threads"/> threads in each of <paramref name="rounds"/> rounds. All threads start a round together.
    /// Returns the results of each round.
    /// </summary>
    public static T[][] Run<T>(int threads, int rounds, Func<int, T> action)
    {
        var results = Enumerable.Range(0, rounds).Select(_ => new T[threads]).ToArray();
        Exception? failure = null;

        using var barrier = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(thread => new Thread(() => {
            for (int round = 0; round < rounds; round++)
            {
                barrier.SignalAndWait();

                try
                {
                    results[round][thread] = action(round);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            }
        })).ToList();

        foreach (var worker in workers)
            worker.Start();

        foreach (var worker in workers)
            worker.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);

        return results;
    }
}
