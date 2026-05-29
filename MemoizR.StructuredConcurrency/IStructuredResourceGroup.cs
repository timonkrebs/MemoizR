namespace MemoizR.StructuredConcurrency;

public interface IStructuredResourceGroup
{
    CancellationToken Token { get; }
    void AddResource(IDisposable resource);
    void AddResource(IAsyncDisposable resource);
}
