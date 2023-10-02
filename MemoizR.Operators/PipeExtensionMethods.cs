using MemoizR.Reactive;

namespace MemoizR.Operators;

public static class PipeExtensionMethods
{
    // make sure this blocks not other evaluations of the graph while delaying execution
    public static MemoizR<T?> Delay<T>(this MemoizR<T> handlr, TimeSpan time)
    {
        return new MemoizR<T?>(async () =>
        {
            await Task.Delay(time.Milliseconds);
            return handlr.Get();
        }
        , handlr.context, "Delay");
    }

    // make sure this blocks not other evaluations of the graph while delaying execution
    public static MemoizR<T?> Debounce<T>(this MemoizR<T> handlr, TimeSpan time)
    {
        var now = DateTime.UtcNow;
        var cancel = new CancellationTokenSource();
        var s = new Signal<T?>(handlr.value, handlr.context, "Debounce");
        new Reaction<T>(async () =>
        {
            var value = handlr.Get();
            cancel.Cancel();
            cancel = new CancellationTokenSource();
            await Task.Delay(time.Milliseconds, cancel.Token);
            s.Set(value);
            return value;
        }, handlr.context);

        return new MemoizR<T?>(s.Get, handlr.context, "DebounceSignal");
    }
}
