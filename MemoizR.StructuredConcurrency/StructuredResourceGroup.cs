namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResourceGroup : IStructuredResourceGroup
{
    private readonly List<object> resources = new();
    private readonly Lock mutex = new();
    private bool isDisposed;

    public CancellationToken Token { get; }

    public StructuredResourceGroup(CancellationToken token)
    {
        Token = token;
    }

    public void AddResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (mutex)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            resources.Add(resource);
        }
    }

    public void AddResource(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (mutex)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
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
            isDisposed = true;
        }

        resourcesToDispose.Reverse();

        List<Exception>? exceptions = null;

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
            catch (Exception ex)
            {
                (exceptions ??= new()).Add(ex);
            }
        }
        if (exceptions != null)
        {
            throw new AggregateException(exceptions);
        }
    }
}
