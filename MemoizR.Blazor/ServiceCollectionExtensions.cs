using Microsoft.Extensions.DependencyInjection;

namespace MemoizR;

public static class MemoizRServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped <see cref="MemoFactory"/>: one reactive graph per circuit on Blazor
    /// Server (circuits are genuinely multi-threaded -- exactly what MemoizR's cross-flow
    /// guarantees are for), one per app on WebAssembly. Inject it into components (built in
    /// for <see cref="MemoizRComponentBase"/>) or services.
    /// </summary>
    public static IServiceCollection AddMemoizR(this IServiceCollection services, MemoFactoryOptions options = MemoFactoryOptions.None)
    {
        return services.AddScoped(_ => new MemoFactory(null, options));
    }
}
