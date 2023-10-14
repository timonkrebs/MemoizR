namespace MemoizR.Reactive;

public static class SignalOperatorExtensionMethods
{
    // make sure this blocks not other evaluations of the graph while delaying execution
    public static MemoizR<T?> Delay<T>(this MemoizR<T> handlr, TimeSpan time)
    {
        return new MemoizR<T?>(async () =>
        {
            await Task.Delay(time);
            return await handlr.Get();
        }
        , handlr.Context, "Delay");
    }

    // make sure this blocks not other evaluations of the graph while delaying execution
    public static MemoizR<T?> Debounce<T>(this MemoizR<T> handlr, TimeSpan time)
    {
        var cancel = new CancellationTokenSource();
        var s = new Signal<T?>(handlr.Value, handlr.Context, "Debounce");
        new Reaction(async () =>
        {
            cancel.Cancel();
            cancel = new CancellationTokenSource();
            await Task.Delay(time, cancel.Token);
            var value = await handlr.Get();
            var task = s.Set(value);
        }, handlr.Context);

        return new MemoizR<T?>(s.Get, handlr.Context, "DebounceSignal");
    }
}
