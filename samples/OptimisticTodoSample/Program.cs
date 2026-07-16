using MemoizR;
using OptimisticTodoSample.Components;
using OptimisticTodoSample.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// One reactive graph per circuit; the sample's state and backend hang off the same scope.
builder.Services.AddMemoizR();
builder.Services.AddScoped<ITodoApi, FlakyTodoApi>();
builder.Services.AddScoped<TodoState>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
