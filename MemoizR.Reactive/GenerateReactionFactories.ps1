$classHeader = @"
namespace MemoizR.Reactive;

public sealed class ReactionBuilder
{
    private readonly MemoFactory memoFactory;
    private readonly SynchronizationContext? synchronizationContext;

    private string label;
    private TimeSpan debounceTime = TimeSpan.FromMilliseconds(10);

    public ReactionBuilder(MemoFactory memoFactory, SynchronizationContext? synchronizationContext, string label)
    {
        this.memoFactory = memoFactory;
        this.synchronizationContext = synchronizationContext;
        this.label = label;
    }

    public ReactionBuilder AddDebounceTime(TimeSpan debounceTime)
    {
        this.debounceTime = debounceTime;
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
        lock (memoFactory)
        {
            return new Reaction(async () => action($actionName), memoFactory.Context, synchronizationContext)
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
        return new AdvancedReaction(fn, memoFactory.Context, synchronizationContext)
        {
            Label = label,
            DebounceTime = debounceTime
        };
    }
}
"@

Out-File -FilePath .\ReactionBuilder.cs -InputObject $classFooter -Encoding ASCII -Append