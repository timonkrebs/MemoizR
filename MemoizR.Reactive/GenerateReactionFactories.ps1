$classHeader = @"
namespace MemoizR.Reactive;

public sealed class ReactionBuilder
{
    private readonly MemoFactory memoFactory;
    private IExecutor? executor;

    private string label;
    private TimeSpan debounceTime = TimeSpan.FromMilliseconds(10);

    public ReactionBuilder(MemoFactory memoFactory, IExecutor? executor, string label)
    {
        this.memoFactory = memoFactory;
        this.executor = executor;
        this.label = label;
    }

    public ReactionBuilder AddDebounceTime(TimeSpan debounceTime)
    {
        this.debounceTime = debounceTime;
        return this;
    }

    /// <summary>
    /// Overrides the factory-level executor for the reactions built by THIS builder -- per-node
    /// executor selection, like a Swift actor declaring its own unownedExecutor (SE-0392).
    /// </summary>
    public ReactionBuilder AddExecutor(IExecutor executor)
    {
        this.executor = executor;
        return this;
    }

"@

Out-File -FilePath .\ReactionBuilder.cs -InputObject $classHeader -Encoding ASCII

for ($i = 1; $i -le 16; $i++) {
    $memoTypes = 1..$i | ForEach-Object { "T$_" }
    $memoNames = 1..$i | ForEach-Object { "await memo$_.Get()" }
    $parameter = 1..$i | ForEach-Object { "IStateGetR<T$_> memo$_" }
    $actionType = ($memoTypes -join ", ")
    $parameterNames = ($parameter -join ", ")
    $actionName = ($memoNames -join ", ")
    $method = @"
    public Reaction CreateReaction<$actionType>($parameterNames, Action<$actionType> action)
    {
        lock (memoFactory.Lock)
        {
            return new(async () => action($actionName), memoFactory.Context, synchronizationContext)
            {
                Label = label,
                DebounceTime = debounceTime
            };
        }
    }

"@

    Out-File -FilePath .\ReactionBuilder.cs -InputObject $method -Encoding ASCII -Append
}

$classFooter = @"
    public AdvancedReaction CreateAdvancedReaction(Func<Task> fn)
    {
        return new(fn, memoFactory.Context, synchronizationContext)
        {
            Label = label,
            DebounceTime = debounceTime
        };
    }
}
"@

Out-File -FilePath .\ReactionBuilder.cs -InputObject $classFooter -Encoding ASCII -Append