namespace MemoizR;

public enum CacheState
{
    CacheClean = 0,
    CacheCheck = 1,
    CacheDirty = 2
}

public class MemoHandlR<T>
{
    /** current capture context for identifying @reactive sources (other reactive elements) and cleanups
 * - active while evaluating a reactive function body  */
    protected dynamic CurrentReaction = null;
    protected MemoHandlR<dynamic>[] CurrentGets = Array.Empty<MemoHandlR<dynamic>>();
    protected int CurrentGetsIndex = 0;

    protected MemoizR<dynamic>[] observers = Array.Empty<MemoizR<dynamic>>(); // nodes that have us as sources (down links)

    internal MemoHandlR<dynamic>[] sources = Array.Empty<MemoHandlR<dynamic>>(); // sources in reference order, not deduplicated (up links)

    protected Func<T> fn = () => default;
    protected T value = default;
    protected string label;
    protected CacheState state = CacheState.CacheClean;

    public MemoHandlR(string label = "label")
    {
        this.label = label;
    }

    internal void Stale(CacheState state)
    {
        if (this.state < state)
        {
            this.state = state;

            if (observers.Length > 0)
            {
                for (int i = 0; i < observers.Length; i++)
                {
                    observers[i].Stale(CacheState.CacheCheck);
                }
            }
        }
    }

    /** update() if dirty, or a parent turns out to be dirty. */
    internal void UpdateIfNecessary()
    {
        // If we are potentially dirty, see if we have a parent who has actually changed value
        if (state == CacheState.CacheCheck)
        {
            foreach (var source in this.sources!)
            {
                source.UpdateIfNecessary(); // updateIfNecessary() can change this.state
                if (this.state == CacheState.CacheDirty)
                {
                    // Stop the loop here so we won't trigger updates on other parents unnecessarily
                    // If our computation changes to no longer use some sources, we don't
                    // want to update() a source we used last time, but now don't use.
                    break;
                }
            }
        }

        // If we were already dirty or marked dirty by the step above, update.
        if (this.state == CacheState.CacheDirty)
        {
            this.Update();
        }

        // By now, we're clean
        this.state = CacheState.CacheClean;
    }

    /** run the computation fn, updating the cached value */
    private void Update()
    {
        var oldValue = value;

        /* Evalute the reactive function body, dynamically capturing any other reactives used */
        var prevReaction = CurrentReaction;
        var prevGets = CurrentGets;
        var prevIndex = CurrentGetsIndex;

        CurrentReaction = this;
        CurrentGets = Array.Empty<MemoHandlR<object>>();
        CurrentGetsIndex = 0;

        try
        {
            value = fn();

            // if the sources have changed, update source & observer links
            if (CurrentGets.Length > 0)
            {
                // remove all old sources' .observers links to us
                this.RemoveParentObservers(CurrentGetsIndex);
                // update source up links
                if (this.sources.Any() && CurrentGetsIndex > 0)
                {
                    this.sources = sources.Take(CurrentGetsIndex).Union(CurrentGets).ToArray();
                }
                else
                {
                    sources = CurrentGets;
                }

                for (var i = CurrentGetsIndex; i < this.sources.Length; i++)
                {
                    // Add ourselves to the end of the parent .observers array
                    var source = sources[i];
                    if (!source.observers.Any())
                    {
                        source.observers = (new[] { this }).Cast<MemoizR<dynamic>>().ToArray();
                    }
                    else
                    {
                        source.observers = source.observers.Union((new[] { this }).Cast<MemoizR<dynamic>>()).ToArray();
                    }
                }
            }
            else if (this.sources.Any() && CurrentGetsIndex < this.sources.Length)
            {
                // remove all old sources' .observers links to us
                this.RemoveParentObservers(CurrentGetsIndex);
                this.sources = sources.Take(CurrentGetsIndex).ToArray();
            }
        }
        finally
        {
            CurrentGets = prevGets;
            CurrentReaction = prevReaction;
            CurrentGetsIndex = prevIndex;
        }

        // handles diamond depenendencies if we're the parent of a diamond.
        if (oldValue != null && oldValue.Equals(this.value) && this.observers.Length > 0)
        {
            // We've changed value, so mark our children as dirty so they'll reevaluate
            for (int i = 0; i < this.observers.Length; i++)
            {
                var observer = this.observers[i];
                observer.state = CacheState.CacheDirty;
            }
        }

        // We've rerun with the latest values from all of our sources.
        // This means that we no longer need to update until a signal changes
        this.state = CacheState.CacheClean;
    }

    private void RemoveParentObservers(int index)
    {
        if (!sources.Any()) return;
        for (var i = index; i < sources.Length; i++)
        {
            var source = sources[i]; // We don't actually delete sources here because we're replacing the entire array soon
            var swap = Array.FindIndex(source.observers, (v) => v.Equals(this));
            source.observers![swap] = source.observers![source.observers!.Length - 1];
            source.observers = source.observers.SkipLast(1).ToArray();
        }
    }
}

public class MemoSetR<T> : MemoHandlR<T>
{
    // equals = defaultEquality;

    public MemoSetR(T value) : base()
    {
        this.value = value;
    }

    public void Set(T value)
    {
        if (this.value != null && !this.value.Equals(value))
        {
            if (this.observers.Length > 0)
            {
                for (int i = 0; i < this.observers.Length; i++)
                {
                    var observer = this.observers[i];
                    observer.Stale(CacheState.CacheDirty);
                }
            }
            this.value = value;
        }
    }
}


public class MemoizR<T> : MemoHandlR<T>
{
    MemoizR(Func<T> fn) : base()
    {
        this.fn = fn;
        this.state = CacheState.CacheDirty;
    }

    public T Get()
    {
        if (CurrentReaction != null)
        {
            if (
              !CurrentGets.Any() &&
              CurrentReaction.sources.Any() &&
              CurrentReaction.sources[CurrentGetsIndex].Equals(this)
            )
            {
                CurrentGetsIndex++;
            }
            else
            {
                if (!CurrentGets.Any()) CurrentGets = (new[] { this }).Cast<MemoizR<dynamic>>().ToArray();
                else CurrentGets = CurrentGets.Union(new[] { this }.Cast<MemoHandlR<dynamic>>()).ToArray();
            }
        }

        this.UpdateIfNecessary();
        return this.value;
    }
}