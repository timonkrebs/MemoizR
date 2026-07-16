using System.Collections.Concurrent;
using OptimisticTodoSample.Services;

namespace OptimisticTodoSample.Tests;

// The deterministic "server": every save parks on a gate keyed by its text until the test
// confirms or rejects it, so the optimistic window is provably open at the assertion points.
// Keyed, not queued: overlapping action bodies run concurrently, so arrival order is NOT
// submission order -- and Confirm/Reject WAIT for the save to arrive, because a body applies
// its optimistic patch (which the test observes) before it reaches the server.
internal sealed class GatedTodoApi : ITodoApi
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pending = new();

    public Task<string> SaveAsync(string text, CancellationToken token)
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[text] = gate;
        return gate.Task;
    }

    public async Task Confirm(string text)
    {
        (await TakeAsync(text)).SetResult(text);
    }

    public async Task Reject(string text, string message)
    {
        (await TakeAsync(text)).SetException(new InvalidOperationException(message));
    }

    private async Task<TaskCompletionSource<string>> TakeAsync(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (pending.TryRemove(text, out var gate))
            {
                return gate;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"no save for \"{text}\" arrived at the server");
    }
}
