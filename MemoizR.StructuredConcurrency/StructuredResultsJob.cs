using System.Collections.Concurrent;

namespace MemoizR.StructuredConcurrency;

public sealed class StructuredResultsJob<T> : StructuredJobBase<ConcurrentDictionary<int, T>>
{
    private IList<IMemoHandlR> allSources = [];

    private readonly IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns;
    private readonly Context context;
    private readonly ConcurrentMap<T> @this;
    private readonly CancellationTokenSource cancellationTokenSource;

    public StructuredResultsJob(IReadOnlyCollection<Func<CancellationTokenSource, Task<T>>> fns, Context context, ConcurrentMap<T> @this)
    {
        this.fns = fns;
        this.context = context;
        this.@this = @this;
        this.result = new();
        this.cancellationTokenSource = context.CancellationTokenSource!;
    }

    protected override Task AddConcurrentWork()
    {
        tasks.AddRange(fns
        .Select(async (x, i) => await Task.Run(async () =>
            {
                context.ForceNewScope();
                context.ReactionScope.CurrentReaction = @this;
                try
                {
                    result!.TryAdd(i, await x(cancellationTokenSource));
                }
                catch
                {
                    cancellationTokenSource.Cancel();
                    throw;
                }
                lock (this)
                {
                    // if the sources have changed, update source & observer links
                    if (context.ReactionScope.CurrentGets.Length > 0)
                    {

                        // update source up links
                        if (allSources.Any() && context.ReactionScope.CurrentGetsIndex > 0)
                        {
                            allSources = [.. allSources, .. context.ReactionScope.CurrentGets];
                        }
                        else
                        {
                            allSources = context.ReactionScope.CurrentGets;
                        }

                        for (var i = context.ReactionScope.CurrentGetsIndex; i < allSources.Count(); i++)
                        {
                            // Add ourselves to the end of the parent .observers array
                            var source = allSources[i];
                            source.Observers = !source.Observers.Any()
                                ? [new(@this)]
                                : [.. source.Observers, new(@this)];
                        }
                    }
                }

            }, cancellationTokenSource.Token)
        ));
        return Task.CompletedTask;
    }

    protected override void HandleSubscriptions()
    {
        @this.Sources = allSources.Distinct().ToArray();
    }
}
