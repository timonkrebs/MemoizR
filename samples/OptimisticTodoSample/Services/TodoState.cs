using System.Collections.Immutable;
using MemoizR;
using MemoizR.Reactive;

namespace OptimisticTodoSample.Services;

/// <summary>
/// The reactive graph behind the todo UI (ADR 0007), one instance per circuit. The source of
/// truth is an atomically updatable list; <see cref="Todos"/> is its optimistic view (source +
/// in-flight patches), and <see cref="AddTodo"/> is the process-layer action: it instantly
/// projects the new todo as pending, saves it on the server, confirms atomically, and rolls
/// back structurally when the server rejects -- no manual recovery anywhere.
/// </summary>
public sealed class TodoState
{
    private readonly EagerRelativeSignal<ImmutableList<TodoItem>> source;

    public TodoState(MemoFactory memo, ITodoApi api)
    {
        source = memo.CreateEagerRelativeSignal(ImmutableList<TodoItem>.Empty);
        Todos = memo.CreateOptimistic<ImmutableList<TodoItem>>(source, "Todos");
        AddTodo = memo.CreateAction<string>(async (text, ctx) =>
        {
            // 1. Instant projection: the UI shows the todo (muted) before the network moves.
            await ctx.Apply(Todos, list => list.Add(new TodoItem(text, Pending: true)));

            // 2. The process step: the real save, cancellable via the run's token.
            var confirmed = await api.SaveAsync(text, ctx.Token);

            // 3. Confirm atomically: overlapping runs compose instead of clobbering. When the
            //    run ends its patch is dropped; on a fault the drop IS the rollback and the
            //    source was never touched.
            await source.Set(list => list.Add(new TodoItem(confirmed, Pending: false)));
        }, "AddTodo");
    }

    public OptimisticState<ImmutableList<TodoItem>> Todos { get; }

    public ReactiveAction<string> AddTodo { get; }
}
