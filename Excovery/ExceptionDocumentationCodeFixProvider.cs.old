using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Excovery;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ExceptionDocumentationCodeFixProvider)), Shared]
public sealed class ExceptionDocumentationCodeFixProvider : CodeFixProvider
{
    private const string Title = "Add missing <exception> documentation";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(
            ExceptionDocumentationAnalyzer.DiagnosticId,
            ExceptionDocumentationAnalyzer.CallbackDiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var declaration = node.AncestorsAndSelf()
            .FirstOrDefault(candidate => candidate is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
        if (declaration is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                Title,
                cancellationToken => AddMissingTagsAsync(context.Document, declaration, cancellationToken),
                equivalenceKey: Title),
            diagnostic);
    }

    private static async Task<Document> AddMissingTagsAsync(
        Document document,
        SyntaxNode declaration,
        CancellationToken cancellationToken)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null || semanticModel is null || root is null)
            return document;

        var analyzerDiagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ExceptionDocumentationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);

        var exceptionTypes = analyzerDiagnostics
            .Where(diagnostic => (diagnostic.Id == ExceptionDocumentationAnalyzer.DiagnosticId ||
                                  diagnostic.Id == ExceptionDocumentationAnalyzer.CallbackDiagnosticId) &&
                                 diagnostic.Location.SourceTree == root.SyntaxTree &&
                                 declaration.Span.Contains(diagnostic.Location.SourceSpan))
            .Select(diagnostic => diagnostic.Properties.TryGetValue(
                ExceptionDocumentationAnalyzer.ExceptionTypePropertyKey, out var typeId) ? typeId : null)
            .Where(typeId => !string.IsNullOrWhiteSpace(typeId))
            .Select(typeId => DocumentationCommentId.GetFirstSymbolForDeclarationId(typeId!, compilation) as ITypeSymbol)
            .Where(type => type is not null)
            .Cast<ITypeSymbol>()
            .GroupBy(type => DocumentationCommentId.CreateDeclarationId(type), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        if (exceptionTypes.Length == 0)
            return document;

        var declarationStart = declaration.SpanStart;
        var line = sourceText.Lines.GetLineFromPosition(declarationStart);
        var indentation = sourceText.ToString(TextSpan.FromBounds(line.Start, declarationStart));
        var newline = GetNewLine(sourceText, line);
        var tags = exceptionTypes.Select(type =>
        {
            var typeName = type.ToMinimalDisplayString(
                semanticModel,
                declarationStart,
                SymbolDisplayFormat.MinimallyQualifiedFormat);
            var cref = EscapeXmlAttribute(typeName);
            return $"/// <exception cref=\"{cref}\"></exception>";
        });
        var insertion = string.Join(newline + indentation, tags) + newline + indentation;
        var changedText = sourceText.WithChanges(new TextChange(new TextSpan(declarationStart, 0), insertion));
        return document.WithText(changedText);
    }

    private static string GetNewLine(SourceText sourceText, TextLine line)
    {
        if (line.EndIncludingLineBreak > line.End)
            return sourceText.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));

        foreach (var existingLine in sourceText.Lines)
            if (existingLine.EndIncludingLineBreak > existingLine.End)
                return sourceText.ToString(TextSpan.FromBounds(existingLine.End, existingLine.EndIncludingLineBreak));

        return "\n";
    }

    private static string EscapeXmlAttribute(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
