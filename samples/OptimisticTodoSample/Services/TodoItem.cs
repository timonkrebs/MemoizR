namespace OptimisticTodoSample.Services;

/// <summary>One todo row. <see cref="Pending"/> marks an optimistic projection that the
/// server has not confirmed yet; the UI renders it muted.</summary>
public sealed record TodoItem(string Text, bool Pending);
