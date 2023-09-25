namespace MemoizR.Operators;

public static class PipeExtensionMethods
{
    public static MemoizR<T?> Delay<T>(this MemoizR<T> handlr, TimeSpan time)
    {
        return new MemoizR<T?>(async () =>
        {
            await Task.Delay(time.Milliseconds);
            return handlr.Get();
        }
        , handlr.context, "Delay");
    }

    public static MemoizR<T?> Debounce<T>(this MemoizR<T> handlr, TimeSpan time)
    {
        var now = DateTime.UtcNow;
        var s = new Signal<T?>(async () =>
        {
            var timeLeft = DateTime.UtcNow.CompareTo(now);
            if(timeLeft > time.Microseconds)
            {
                return handlr.Get();
            }
            await Task.Delay(timeLeft);
            return handlr.Get();
        }
        , handlr.context, "Debounce");

        return new MemoizR<T?>(s.Get, handlr.context, "DebounceSignal");
    }
}
