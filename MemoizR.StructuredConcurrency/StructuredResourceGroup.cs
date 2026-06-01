namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResourceGroup : IStructuredResourceGroup
{
    private readonly List<object> resources = new();
    private readonly Lock mutex = new();

    public CancellationToken Token { get; }

    public StructuredResourceGroup(CancellationToken token)
    {
        Token = token;
    }

    public void AddResource(IDisposable resource)
    {
        lock (mutex)
        {
            resources.Add(resource);
        }
    }

    public void AddResource(IAsyncDisposable resource)
    {
        lock (mutex)
        {
            resources.Add(resource);
        }
    }

    public async Task DisposeResources()
    {
        List<object> resourcesToDispose;
        lock (mutex)
        {
            resourcesToDispose = new List<object>(resources);
            resources.Clear();
        }

        resourcesToDispose.Reverse();

        foreach (var resource in resourcesToDispose)
        {
            try
            {
                if (resource is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (resource is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Exceptions during disposal are typically ignored in structured concurrency
                // or aggregated. For now, we'll follow the pattern of ignoring them to avoid
                // masking the original exception.
            }
        }
    }
}
