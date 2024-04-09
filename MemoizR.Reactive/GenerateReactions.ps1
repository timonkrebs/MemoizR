function GenerateReactionClass {
    param (
        [int]$numMemos
    )

    $genericTypes = @()
    $memoFields = @()
    $memoParameters = @()
    $memoArguments = @()
    $taskVariables = @()
    $whenAllVariables = @()
    $actionParameters = @()

    for ($i = 1; $i -le $numMemos; $i++) {
        $genericTypes += "T$i"
        $memoFields += "private readonly IStateGetR<T$i> memo$i;"
        $memoParameters += "IStateGetR<T$i> memo$i"
        $memoArguments += "this.memo$i = memo$i"
        $taskVariables += "var task$i = memo$i.Get();"
        $whenAllVariables += "task$i"
        $actionParameters += "await task$i"
    }

    $genericTypesString = $genericTypes -join ", "
    $memoFieldsString = $memoFields -join "`n    "
    $memoParametersString = $memoParameters -join ",`n                      "
    $memoArgumentsString = $memoArguments -join ";`n        "
    $taskVariablesString = $taskVariables -join "`r`n        "
    $actionParameterTypesString =
    $actionParametersString = $actionParameters -join ", "

    $class = @"
public sealed class Reaction<$genericTypesString> : ReactionBase
{
    $memoFieldsString
    private readonly Action<$genericTypesString> action;

    internal Reaction($memoParametersString,    
                      Action<$genericTypesString> action, 
                      Context context, 
                      SynchronizationContext? synchronizationContext = null)
        : base(context, synchronizationContext)
    {
        $memoArgumentsString;
        this.action = action;

        Stale(CacheState.CacheDirty);
    }

    protected override async Task Execute()
    {
        $taskVariablesString
        await Task.WhenAll($($whenAllVariables -join ', '));
        action($actionParametersString);
    }
}

"@

    return $class
}

$classHeader = @"
namespace MemoizR.Reactive;

"@

Out-File -FilePath .\Reaction.cs -InputObject $classHeader -Encoding ASCII

# Generate classes for reactions with 1 to 10 memos
for ($i = 1; $i -le 16; $i++) {
    $classContent = GenerateReactionClass $i
    Out-File -FilePath .\Reaction.cs -InputObject $classContent -Encoding ASCII -Append
}