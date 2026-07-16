using Bunit;
using MemoizR;
using Microsoft.Extensions.DependencyInjection;
using OptimisticTodoSample.Components;
using OptimisticTodoSample.Services;

namespace OptimisticTodoSample.Tests;

// The bUnit half of ADR 0007 phase 4: the optimistic-todo component driven through Blazor's
// test renderer, against a gated fake server so every optimistic window is deterministic.
// The assertions are Solid 2.0's transition lifecycle table, observed through rendered markup.
public class TodoListTests : BunitContext
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly GatedTodoApi api = new();

    public TodoListTests()
    {
        Services.AddMemoizR();
        Services.AddScoped<ITodoApi>(_ => api);
        Services.AddScoped<TodoState>();
    }

    private IRenderedComponent<TodoList> RenderTodoList() => Render<TodoList>();

    // Find-and-trigger runs as ONE dispatch on the renderer's context: the reactive bindings
    // re-render from background flows, so an element found outside it can go stale before the
    // event fires (bUnit's UnknownEventHandlerIdException).
    private static Task Submit(IRenderedComponent<TodoList> cut, string text)
    {
        return cut.InvokeAsync(() =>
        {
            cut.Find("input").Input(text);
            cut.Find("form").Submit();
        });
    }

    [Fact]
    public async Task Add_ProjectsOptimistically_DisablesTheButton_ThenConfirms()
    {
        var cut = RenderTodoList();

        await Submit(cut, "write docs");

        // 2. Action triggered: the todo renders instantly, muted, while the server is still
        //    deciding; the action's reactive IsPending flag disables the button.
        cut.WaitForAssertion(() =>
        {
            var item = cut.Find("li");
            Assert.Contains("write docs", item.TextContent);
            Assert.Contains("pending", item.ClassList);
            Assert.True(cut.Find("button").HasAttribute("disabled"));
        }, WaitTimeout);

        // 4. Server commit: the confirmed item replaces the projection, the button re-enables.
        await api.Confirm("write docs");
        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll("li");
            var item = Assert.Single(items);
            Assert.Contains("write docs", item.TextContent);
            Assert.Contains("settled", item.ClassList);
            Assert.False(cut.Find("button").HasAttribute("disabled"));
        }, WaitTimeout);
    }

    [Fact]
    public async Task Add_RollsBackAndShowsTheError_WhenTheServerRejects()
    {
        var cut = RenderTodoList();

        await Submit(cut, "deploy on friday");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li")), WaitTimeout);

        // 5. Rollback: the projection vanishes -- no manual recovery in the component beyond
        //    showing the error the run's Completion carried.
        await api.Reject("deploy on friday", "deploys are for mondays");
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("li"));
            Assert.Contains("deploys are for mondays", cut.Find(".error").TextContent);
            Assert.False(cut.Find("button").HasAttribute("disabled"));
        }, WaitTimeout);
    }

    [Fact]
    public async Task OverlappingAdds_RollBackOnlyTheRejectedProjection()
    {
        var cut = RenderTodoList();

        await Submit(cut, "first");
        await Submit(cut, "second");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("li").Count), WaitTimeout);

        // The first save fails, the second succeeds: structural rollback removes only the
        // rejected run's patch -- the surviving projection and its confirmation are untouched.
        await api.Reject("first", "rejected");
        await api.Confirm("second");
        cut.WaitForAssertion(() =>
        {
            var item = Assert.Single(cut.FindAll("li"));
            Assert.Contains("second", item.TextContent);
            Assert.Contains("settled", item.ClassList);
        }, WaitTimeout);
    }
}
