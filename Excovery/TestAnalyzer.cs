using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        "EX0001",
        "Test analyzer",
        "Excovery analyzer is running",
        "Testing",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.None);

        context.RegisterSyntaxTreeAction(ctx =>
        {
            ctx.ReportDiagnostic(
                Diagnostic.Create(Rule, ctx.Tree.GetRoot().GetLocation()));
        });
    }
}
