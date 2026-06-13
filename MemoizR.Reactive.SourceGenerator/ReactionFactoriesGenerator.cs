using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace MemoizR.Reactive.SourceGenerator;

/// <summary>
/// Emits the strongly-typed <c>CreateReaction&lt;T1, ..., Tn&gt;</c> arity overloads (n = 1..16)
/// for both <c>ReactionBuilder</c> and the <c>ReactiveMemoFactory</c> extension surface, into the
/// generated halves of those partial classes.
/// </summary>
/// <remarks>
/// Replaces <c>MemoizR.Reactive/GenerateReactionFactories.ps1</c>: the overloads are now produced
/// at compile time instead of being a hand-run script whose output was committed and then edited
/// by hand (which let the script and the real source drift apart). Editing the shape of an
/// overload now means editing this generator, and the arity ceiling lives in exactly one place.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ReactionFactoriesGenerator : IIncrementalGenerator
{
    // The upper bound the PowerShell script produced. System.Action also tops out at 16 type
    // parameters, so this is the framework ceiling for the action delegate as well.
    private const int MaxArity = 16;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The output depends on nothing in the user's compilation, so it is emitted once at
        // post-initialization rather than wired through the incremental pipeline.
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("ReactionBuilder.CreateReaction.g.cs", SourceText.From(GenerateReactionBuilder(), Encoding.UTF8));
            ctx.AddSource("ReactiveMemoFactory.CreateReaction.g.cs", SourceText.From(GenerateReactiveMemoFactory(), Encoding.UTF8));
        });
    }

    private static string GenerateReactionBuilder()
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb, "", "namespace MemoizR.Reactive;");
        sb.AppendLine("public sealed partial class ReactionBuilder");
        sb.AppendLine("{");

        for (var n = 1; n <= MaxArity; n++)
        {
            if (n > 1)
            {
                sb.AppendLine();
            }

            AppendBuilderOverload(sb, n);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // One dependency: a plain tracked Get on the reaction's own scope captures it -- there is
    // nothing to register up front or evaluate in parallel against. Two or more: every dependency
    // is registered in parameter order first, then the values are computed in parallel on isolated
    // scopes, and only the action is marshalled to the SynchronizationContext. See ReactionBuilder.
    private static void AppendBuilderOverload(StringBuilder sb, int n)
    {
        var typeNames = TypeNames(n);
        var memoNames = MemoNames(n);
        var typeList = string.Join(", ", typeNames);
        var parameters = string.Join(", ", typeNames.Zip(memoNames, (t, m) => $"IStateGetR<{t}> {m}"));

        sb.AppendLine($"    public Reaction CreateReaction<{typeList}>({parameters}, Action<{typeList}> action)");
        sb.AppendLine("    {");
        sb.AppendLine("        return Build(async () =>");
        sb.AppendLine("        {");

        if (n == 1)
        {
            sb.AppendLine("            var v1 = await memo.Get();");
            sb.AppendLine("            await InvokeActionAsync(() => action(v1));");
        }
        else
        {
            foreach (var memo in memoNames)
            {
                sb.AppendLine($"            RegisterDependency({memo});");
            }

            for (var i = 1; i <= n; i++)
            {
                sb.AppendLine($"            var t{i} = EvaluateOnOwnScopeAsync(memo{i});");
            }

            sb.AppendLine($"            await Task.WhenAll({Join("t", n)});");

            for (var i = 1; i <= n; i++)
            {
                sb.AppendLine($"            var v{i} = await t{i};");
            }

            sb.AppendLine($"            await InvokeActionAsync(() => action({Join("v", n)}));");
        }

        sb.AppendLine("        });");
        sb.AppendLine("    }");
    }

    private static string GenerateReactiveMemoFactory()
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb, "using MemoizR.Reactive;", "", "namespace MemoizR;");
        sb.AppendLine("public static partial class ReactiveMemoFactory");
        sb.AppendLine("{");

        for (var n = 1; n <= MaxArity; n++)
        {
            if (n > 1)
            {
                sb.AppendLine();
            }

            AppendFactoryOverload(sb, n);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // Factory-level sugar: identical to BuildReaction().CreateReaction(..) with the default label
    // and debounce. Pure delegation, so a single uniform shape covers every arity.
    private static void AppendFactoryOverload(StringBuilder sb, int n)
    {
        var typeNames = TypeNames(n);
        var memoNames = MemoNames(n);
        var typeList = string.Join(", ", typeNames);
        var parameters = string.Join(", ", typeNames.Zip(memoNames, (t, m) => $"IStateGetR<{t}> {m}"));
        var arguments = string.Join(", ", memoNames);

        sb.AppendLine($"    public static Reaction CreateReaction<{typeList}>(this MemoFactory memoFactory, {parameters}, Action<{typeList}> action)");
        sb.AppendLine("    {");
        sb.AppendLine($"        return memoFactory.BuildReaction().CreateReaction({arguments}, action);");
        sb.AppendLine("    }");
    }

    // The single-dependency overload mirrors the original hand-written signature, which used the
    // unsuffixed names "T"/"memo" rather than "T1"/"memo1".
    private static string[] TypeNames(int n) =>
        n == 1 ? new[] { "T" } : Enumerable.Range(1, n).Select(i => $"T{i}").ToArray();

    private static string[] MemoNames(int n) =>
        n == 1 ? new[] { "memo" } : Enumerable.Range(1, n).Select(i => $"memo{i}").ToArray();

    private static string Join(string prefix, int n) =>
        string.Join(", ", Enumerable.Range(1, n).Select(i => $"{prefix}{i}"));

    private static void AppendFileHeader(StringBuilder sb, params string[] usingsThenNamespace)
    {
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Threading.Tasks;");

        foreach (var line in usingsThenNamespace)
        {
            sb.AppendLine(line);
        }

        sb.AppendLine();
    }
}
