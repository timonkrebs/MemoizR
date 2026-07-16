namespace OptimisticTodoSample.Services;

/// <summary>The "server". Returns the confirmed text, or throws to reject the todo.</summary>
public interface ITodoApi
{
    Task<string> SaveAsync(string text, CancellationToken token);
}

/// <summary>
/// The sample backend: slow enough that the optimistic projection is visible, and it rejects
/// any todo containing "fail" so the automatic rollback can be watched live.
/// </summary>
public sealed class FlakyTodoApi : ITodoApi
{
    public async Task<string> SaveAsync(string text, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
        if (text.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("the server rejected this todo");
        }
        return text;
    }
}
