using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Threading;
using System.Xml.Linq;

namespace Excovery;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExceptionDocumentationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "EXC001";
    public const string CallbackDiagnosticId = "EXC002";
    public const string ExceptionTypePropertyKey = "ExceptionTypeId";
    private const string MaxOutwardDelegateSearchCountOption = "excovery.max_outward_delegate_search_count";
    private const string MaxInwardDelegateSearchCountOption = "excovery.max_inward_delegate_search_count";
    private const string MaxInwardDelegateSearchHopCountOption = "excovery.max_inward_delegate_search_hop_count";
    private const int DefaultMaxInwardDelegateSearchHopCount = 3;

    private sealed class CallbackDiagnosticBudget
    {
        private readonly AnalyzerConfigOptionsProvider _optionsProvider;
        private readonly Dictionary<string, int> _outwardCounts = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, int> _inwardCounts = new(System.StringComparer.Ordinal);
        private readonly HashSet<string> _reported = new(System.StringComparer.Ordinal);

        public CallbackDiagnosticBudget(AnalyzerConfigOptionsProvider optionsProvider)
        {
            _optionsProvider = optionsProvider;
        }

        public bool TryClaimWarning(Location location, string exceptionTypeId, bool outward)
        {
            var locationKey = $"{location.SourceTree?.GetHashCode()}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
            var diagnosticKey = $"{locationKey}:{exceptionTypeId}";
            if (!HasCapacity(location, exceptionTypeId, outward))
                return false;

            var counts = outward ? _outwardCounts : _inwardCounts;
            counts.TryGetValue(locationKey, out var currentCount);
            _reported.Add(diagnosticKey);
            counts[locationKey] = currentCount + 1;
            return true;
        }

        public bool HasCapacity(Location location, string exceptionTypeId, bool outward)
        {
            var locationKey = $"{location.SourceTree?.GetHashCode()}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
            if (_reported.Contains($"{locationKey}:{exceptionTypeId}"))
                return false;

            return HasCapacity(location, outward);
        }

        public bool HasCapacity(Location location, bool outward)
        {
            var locationKey = $"{location.SourceTree?.GetHashCode()}:{location.SourceSpan.Start}:{location.SourceSpan.Length}";
            var counts = outward ? _outwardCounts : _inwardCounts;
            counts.TryGetValue(locationKey, out var currentCount);
            var optionName = outward ? MaxOutwardDelegateSearchCountOption : MaxInwardDelegateSearchCountOption;
            var maxCount = location.SourceTree is null
                ? 0
                : GetSearchCount(_optionsProvider.GetOptions(location.SourceTree), optionName);
            return maxCount == 0 || currentCount < maxCount;
        }
    }

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Document escaping exception",
        "Exception '{0}' escaping this member must be documented with an <exception> tag",
        "Documentation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Document each thrown or documented called exception that can escape the member.");

    private static readonly DiagnosticDescriptor CallbackRule = new(
        CallbackDiagnosticId,
        "Document callback exception",
        "Delegate may throw exception '{0}'{1}, which has no matching <exception> tag",
        "Documentation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Document exceptions that can escape when a supplied delegate is invoked.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule, CallbackRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeThrow, SyntaxKind.ThrowStatement);
        context.RegisterSyntaxNodeAction(AnalyzeThrow, SyntaxKind.ThrowExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterCompilationAction(AnalyzeDelegates);
    }

    private static void AnalyzeThrow(SyntaxNodeAnalysisContext context)
    {
        var expression = context.Node switch
        {
            ThrowStatementSyntax statement => statement.Expression,
            ThrowExpressionSyntax throwExpression => throwExpression.Expression,
            _ => null
        };

        if (IsInsideAnonymousFunction(context.Node))
            return;

        var thrownType = context.Node is ThrowStatementSyntax { Expression: null } rethrow
            ? GetRethrownType(rethrow, context)
            : expression is null
                ? null
                : context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
        if (thrownType is null || !IsExceptionType(thrownType, context.Compilation))
            return;

        var member = GetContainingDocumentedMember(context.Node, context);
        if (member is null || HasExceptionDocumentation(member, thrownType, context))
            return;

        context.ReportDiagnostic(CreateDiagnostic(Rule, context.Node.GetLocation(), thrownType));
    }

    private static ITypeSymbol? GetRethrownType(ThrowStatementSyntax statement, SyntaxNodeAnalysisContext context)
    {
        var catchClause = statement.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        if (catchClause is null)
            return null;

        return catchClause.Declaration is null
            ? context.Compilation.GetTypeByMetadataName("System.Exception")
            : context.SemanticModel.GetTypeInfo(catchClause.Declaration.Type, context.CancellationToken).Type;
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (IsInsideAnonymousFunction(context.Node))
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol calledMethod)
            return;

        var member = GetContainingDocumentedMember(invocation, context);
        if (member is null)
            return;

        foreach (var exceptionType in GetDocumentedExceptions(calledMethod, context))
        {
            if (IsCaughtBySurroundingTry(invocation, exceptionType, context) ||
                HasExceptionDocumentation(member, exceptionType, context))
                continue;

            context.ReportDiagnostic(CreateDiagnostic(Rule, invocation.GetLocation(), exceptionType));
        }
    }

    // This pass deliberately follows callback arguments across source declarations,
    // so it needs semantic models for trees other than the tree of one syntax action.
#pragma warning disable RS1030
    private static void AnalyzeDelegates(CompilationAnalysisContext context)
    {
        var diagnosticBudget = new CallbackDiagnosticBudget(context.Options.AnalyzerConfigOptionsProvider);
        foreach (var syntaxTree in context.Compilation.SyntaxTrees)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var root = syntaxTree.GetRoot(context.CancellationToken);
            var semanticModel = context.Compilation.GetSemanticModel(syntaxTree);
            var maxInwardHopCount = GetInwardHopCount(
                context.Options.AnalyzerConfigOptionsProvider.GetOptions(syntaxTree));

            foreach (var lambda in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
            {
                var isDirectCallback = TryGetCallbackParameter(
                    lambda, semanticModel, context.CancellationToken, out var parameter);
                var directArgument = isDirectCallback
                    ? lambda.Ancestors().OfType<ArgumentSyntax>().First()
                    : null;
                var inwardHasCapacity = isDirectCallback &&
                    diagnosticBudget.HasCapacity(directArgument!.Expression.GetLocation(), outward: false);
                var directUses = isDirectCallback && !inwardHasCapacity
                    ? GetDelegateParameterInvocations(parameter, context.Compilation, context.CancellationToken, maxInwardHopCount).ToImmutableArray()
                    : ImmutableArray<(InvocationExpressionSyntax Node, SyntaxNode Declaration)>.Empty;

                if (isDirectCallback && !inwardHasCapacity &&
                    !directUses.Any(use => diagnosticBudget.HasCapacity(use.Node.GetLocation(), outward: true)))
                    continue;

                var exceptionTypes = GetCallbackExceptions(lambda, semanticModel, context.Compilation, context.CancellationToken);
                if (exceptionTypes.Length == 0)
                    continue;

                if (isDirectCallback)
                {
                    if (directUses.IsEmpty)
                        directUses = GetDelegateParameterInvocations(
                            parameter, context.Compilation, context.CancellationToken, maxInwardHopCount).ToImmutableArray();

                    var handledTypes = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (var use in directUses
                                 .OrderBy(item => item.Node.SyntaxTree.FilePath, System.StringComparer.Ordinal)
                                 .ThenBy(item => item.Node.SpanStart))
                    {
                        if (IsInsideNestedFunction(use.Node, use.Declaration))
                            continue;

                        var useSemanticModel = context.Compilation.GetSemanticModel(use.Node.SyntaxTree);
                        var member = GetContainingDocumentedMember(use.Node, useSemanticModel, context.CancellationToken);
                        if (member is null)
                            continue;

                        ReportMissingCallbackExceptions(
                            use.Node,
                            exceptionTypes,
                            member,
                            useSemanticModel,
                            context.Compilation,
                            context.CancellationToken,
                            diagnosticBudget,
                            handledTypes,
                            directArgument!.Expression.GetLocation(),
                            context.ReportDiagnostic);
                    }
                }

                if (TryGetCollectionCallback(lambda, semanticModel, context.CancellationToken,
                        out var collectionLocal, out var collectionMember))
                {
                    if (HasReassignment(collectionLocal, collectionMember, semanticModel, context.CancellationToken))
                        continue;

                    foreach (var consumer in GetCollectionConsumers(collectionLocal, collectionMember, semanticModel,
                                 context.Compilation, context.CancellationToken))
                    {
                        var handledTypes = new HashSet<string>(System.StringComparer.Ordinal);
                        foreach (var use in consumer.Uses.OrderBy(item => item.SyntaxTree.FilePath, System.StringComparer.Ordinal)
                                     .ThenBy(item => item.SpanStart))
                        {
                            var useSemanticModel = context.Compilation.GetSemanticModel(use.SyntaxTree);
                            var member = GetContainingDocumentedMember(use, useSemanticModel, context.CancellationToken);
                            if (member is null)
                                continue;
                            ReportMissingCallbackExceptions(use, exceptionTypes, member, useSemanticModel,
                                context.Compilation, context.CancellationToken, diagnosticBudget, handledTypes,
                                consumer.ArgumentLocation, context.ReportDiagnostic);
                        }
                    }
                }

                if (TryGetLocalDelegate(lambda, semanticModel, context.CancellationToken, out var local, out var containingMember))
                {
                    if (HasReassignment(local, containingMember, semanticModel, context.CancellationToken))
                        continue;

                    var handledTypes = new HashSet<string>(System.StringComparer.Ordinal);
                    var localUses = GetLocalDelegateInvocations(local, containingMember, semanticModel, context.CancellationToken)
                        .Select(use => (Node: use, ArgumentLocation: use.GetLocation()))
                        .Concat(GetLocalDelegateConsumerInvocations(
                                     local,
                                     containingMember,
                                     semanticModel,
                                     context.Compilation,
                                     context.CancellationToken,
                                     maxInwardHopCount))
                        .GroupBy(item => (item.Node.SyntaxTree, item.Node.SpanStart, item.ArgumentLocation.SourceSpan.Start))
                        .Select(group => group.First())
                        .OrderBy(item => item.Node.SyntaxTree.FilePath, System.StringComparer.Ordinal)
                        .ThenBy(item => item.Node.SpanStart);
                    foreach (var useEntry in localUses)
                    {
                        var use = useEntry.Node;
                        var useSemanticModel = context.Compilation.GetSemanticModel(use.SyntaxTree);
                        var member = GetContainingDocumentedMember(use, useSemanticModel, context.CancellationToken);
                        if (member is null)
                            continue;

                        ReportMissingCallbackExceptions(
                            use,
                            exceptionTypes,
                            member,
                            useSemanticModel,
                            context.Compilation,
                            context.CancellationToken,
                            diagnosticBudget,
                            handledTypes,
                            useEntry.ArgumentLocation,
                            context.ReportDiagnostic);
                    }
                }
            }
        }
    }

    private static ImmutableArray<ITypeSymbol> GetCallbackExceptions(
        AnonymousFunctionExpressionSyntax lambda,
        SemanticModel semanticModel,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var exceptions = new Dictionary<string, ITypeSymbol>(System.StringComparer.Ordinal);
        var nodes = lambda.DescendantNodesAndSelf().Where(node => !node.Ancestors()
            .TakeWhile(ancestor => ancestor != lambda)
            .Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

        foreach (var throwNode in nodes.Where(node => node is ThrowStatementSyntax or ThrowExpressionSyntax))
        {
            var thrownType = throwNode switch
            {
                ThrowStatementSyntax { Expression: null } rethrow => GetRethrownType(rethrow, semanticModel, compilation, cancellationToken),
                ThrowStatementSyntax statement when statement.Expression is not null => semanticModel.GetTypeInfo(statement.Expression, cancellationToken).Type,
                ThrowExpressionSyntax throwExpression => semanticModel.GetTypeInfo(throwExpression.Expression, cancellationToken).Type,
                _ => null
            };

            if (thrownType is not null && IsExceptionType(thrownType, compilation) &&
                !IsCaughtBySurroundingTry(throwNode, thrownType, semanticModel, compilation, cancellationToken))
                AddException(exceptions, thrownType);
        }

        foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol calledMethod)
                continue;

            foreach (var exceptionType in GetDocumentedExceptions(calledMethod, compilation, cancellationToken))
                if (!IsCaughtBySurroundingTry(invocation, exceptionType, semanticModel, compilation, cancellationToken))
                    AddException(exceptions, exceptionType);
        }

        return exceptions.Values.ToImmutableArray();
    }

    private static int GetSearchCount(AnalyzerConfigOptions options, string optionName)
    {
        return options.TryGetValue(optionName, out var value) &&
               int.TryParse(value, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : 0;
    }

    private static int GetInwardHopCount(AnalyzerConfigOptions options)
    {
        return options.TryGetValue(MaxInwardDelegateSearchHopCountOption, out var value) &&
               int.TryParse(value, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count
            : DefaultMaxInwardDelegateSearchHopCount;
    }

    private static void AddException(IDictionary<string, ITypeSymbol> exceptions, ITypeSymbol exceptionType)
    {
        var id = DocumentationCommentId.CreateDeclarationId(exceptionType);
        if (id is not null)
            exceptions[id] = exceptionType;
    }

    private static bool TryGetCallbackParameter(
        AnonymousFunctionExpressionSyntax lambda,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out IParameterSymbol parameter)
    {
        var argument = lambda.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
        var invocation = argument?.Parent?.Parent as InvocationExpressionSyntax;
        if (argument is not null && invocation is not null &&
            semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol &&
            semanticModel.GetOperation(argument, cancellationToken) is IArgumentOperation { Parameter: not null } argumentOperation &&
            argumentOperation.Parameter.Type.TypeKind == TypeKind.Delegate)
        {
            parameter = argumentOperation.Parameter;
            return true;
        }

        parameter = null!;
        return false;
    }

    private static IEnumerable<(InvocationExpressionSyntax Node, SyntaxNode Declaration)> GetDelegateParameterInvocations(
        IParameterSymbol parameter,
        Compilation compilation,
        CancellationToken cancellationToken,
        int maxInwardHopCount)
    {
        return GetDelegateParameterInvocations(
            parameter,
            compilation,
            cancellationToken,
            new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default),
            maxInwardHopCount,
            currentHopCount: 0);
    }

    private static IEnumerable<(InvocationExpressionSyntax Node, SyntaxNode Declaration)> GetDelegateParameterInvocations(
        IParameterSymbol parameter,
        Compilation compilation,
        CancellationToken cancellationToken,
        HashSet<IParameterSymbol> visitedParameters,
        int maxInwardHopCount,
        int currentHopCount)
    {
        if (!visitedParameters.Add(parameter))
            yield break;

        foreach (var reference in parameter.ContainingSymbol.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax(cancellationToken);
            var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsInsideNestedFunction(invocation, declaration))
                    continue;

                if (IsInvocationOf(invocation, parameter, semanticModel, cancellationToken))
                {
                    yield return (invocation, declaration);
                }

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    var argumentOperation = semanticModel.GetOperation(argument, cancellationToken) as IArgumentOperation;
                    if (!SymbolEqualityComparer.Default.Equals(
                            semanticModel.GetSymbolInfo(argument.Expression, cancellationToken).Symbol, parameter) ||
                        argumentOperation?.Parameter is not IParameterSymbol forwardedParameter ||
                        forwardedParameter.Type.TypeKind != TypeKind.Delegate ||
                        (maxInwardHopCount > 0 && currentHopCount >= maxInwardHopCount))
                        continue;

                    foreach (var nestedUse in GetDelegateParameterInvocations(
                                 forwardedParameter,
                                 compilation,
                                 cancellationToken,
                                 visitedParameters,
                                 maxInwardHopCount,
                                 currentHopCount + 1))
                        yield return nestedUse;
                }
            }
        }
    }
#pragma warning restore RS1030

    private static bool IsInvocationOf(InvocationExpressionSyntax invocation, ISymbol target, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        ExpressionSyntax expression = invocation.Expression;
        if (expression is MemberAccessExpressionSyntax memberAccess && memberAccess.Name.Identifier.ValueText == "Invoke")
            expression = memberAccess.Expression;

        return SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol, target);
    }

    private static bool TryGetLocalDelegate(
        AnonymousFunctionExpressionSyntax lambda,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ILocalSymbol local,
        out SyntaxNode containingMember)
    {
        var declarator = lambda.Ancestors().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(variable => variable.Initializer?.Value.Span.Contains(lambda.Span) == true);
        var memberNode = declarator?.Ancestors().FirstOrDefault(node =>
            node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
        if (declarator is not null && memberNode is not null && !IsInsideNestedFunction(declarator, memberNode) &&
            semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is ILocalSymbol localSymbol)
        {
            local = localSymbol;
            containingMember = memberNode;
            return true;
        }

        local = null!;
        containingMember = null!;
        return false;
    }

    private static bool TryGetCollectionCallback(
        AnonymousFunctionExpressionSyntax lambda,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ILocalSymbol local,
        out SyntaxNode containingMember)
    {
        var declarator = lambda.Ancestors().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(variable => variable.Initializer?.Value is ObjectCreationExpressionSyntax creation &&
                creation.Initializer?.Expressions.Any(expression => expression.Span.Contains(lambda.Span)) == true);
        var memberNode = declarator?.Ancestors().FirstOrDefault(node =>
            node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
        if (declarator is not null && memberNode is not null && !IsInsideNestedFunction(declarator, memberNode) &&
            semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is ILocalSymbol localSymbol)
        {
            local = localSymbol;
            containingMember = memberNode;
            return true;
        }

        local = null!;
        containingMember = null!;
        return false;
    }

    // Following a parameter into other syntax trees is needed to connect a supplied
    // collection to its foreach invocation sites.
#pragma warning disable RS1030
    private static IEnumerable<(ImmutableArray<InvocationExpressionSyntax> Uses, Location ArgumentLocation)> GetCollectionConsumers(
        ILocalSymbol collection,
        SyntaxNode containingMember,
        SemanticModel semanticModel,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        foreach (var call in containingMember.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (IsInsideNestedFunction(call, containingMember) ||
                semanticModel.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol)
                continue;

            foreach (var argument in call.ArgumentList.Arguments)
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetSymbolInfo(argument.Expression, cancellationToken).Symbol, collection) ||
                    semanticModel.GetOperation(argument, cancellationToken) is not IArgumentOperation { Parameter: { } parameter } ||
                    !TryGetEnumerableDelegateElement(parameter.Type, out _))
                    continue;

                foreach (var reference in parameter.ContainingSymbol.DeclaringSyntaxReferences)
                {
                    var declaration = reference.GetSyntax(cancellationToken);
                    var consumerModel = compilation.GetSemanticModel(declaration.SyntaxTree);
                    var uses = ImmutableArray.CreateBuilder<InvocationExpressionSyntax>();
                    foreach (var loop in declaration.DescendantNodes().OfType<ForEachStatementSyntax>())
                    {
                        if (!SymbolEqualityComparer.Default.Equals(
                                consumerModel.GetSymbolInfo(loop.Expression, cancellationToken).Symbol, parameter) ||
                            consumerModel.GetDeclaredSymbol(loop, cancellationToken) is not ILocalSymbol iterationVariable)
                            continue;

                        foreach (var use in loop.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                            if (IsInvocationOf(use, iterationVariable, consumerModel, cancellationToken))
                                uses.Add(use);
                    }

                    if (uses.Count > 0)
                        yield return (uses.ToImmutable(), argument.Expression.GetLocation());
                }
            }
        }
    }

    private static bool TryGetEnumerableDelegateElement(ITypeSymbol type, out ITypeSymbol elementType)
    {
        if (type is INamedTypeSymbol namedType)
        {
            foreach (var candidate in namedType.AllInterfaces.Concat(new[] { namedType }))
            {
                if (candidate.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>" &&
                    candidate.TypeArguments.Length == 1 && candidate.TypeArguments[0].TypeKind == TypeKind.Delegate)
                {
                    elementType = candidate.TypeArguments[0];
                    return true;
                }
            }
        }

        elementType = null!;
        return false;
    }
#pragma warning restore RS1030

    private static bool HasReassignment(ILocalSymbol local, SyntaxNode containingMember, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return containingMember.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(assignment =>
            SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, local));
    }

    private static IEnumerable<InvocationExpressionSyntax> GetLocalDelegateInvocations(
        ILocalSymbol local,
        SyntaxNode containingMember,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in containingMember.DescendantNodes().OfType<InvocationExpressionSyntax>())
            if (!IsInsideNestedFunction(invocation, containingMember) &&
                IsInvocationOf(invocation, local, semanticModel, cancellationToken))
                yield return invocation;
    }

    private static IEnumerable<(InvocationExpressionSyntax Node, Location ArgumentLocation)> GetLocalDelegateConsumerInvocations(
        ILocalSymbol local,
        SyntaxNode containingMember,
        SemanticModel semanticModel,
        Compilation compilation,
        CancellationToken cancellationToken,
        int maxInwardHopCount)
    {
        foreach (var invocation in containingMember.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (IsInsideNestedFunction(invocation, containingMember) ||
                semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol)
                continue;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                var argumentOperation = semanticModel.GetOperation(argument, cancellationToken) as IArgumentOperation;
                if (!SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetSymbolInfo(argument.Expression, cancellationToken).Symbol,
                        local) ||
                    argumentOperation?.Parameter is not IParameterSymbol callbackParameter ||
                    callbackParameter.Type.TypeKind != TypeKind.Delegate)
                    continue;

                foreach (var use in GetDelegateParameterInvocations(
                             callbackParameter, compilation, cancellationToken, maxInwardHopCount))
                    yield return (use.Node, argument.Expression.GetLocation());
            }
        }
    }

    private static bool IsInsideNestedFunction(SyntaxNode node, SyntaxNode containingMember) =>
        node.Ancestors().TakeWhile(ancestor => ancestor != containingMember)
            .Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    private static void ReportMissingCallbackExceptions(
        InvocationExpressionSyntax use,
        ImmutableArray<ITypeSymbol> exceptionTypes,
        ISymbol member,
        SemanticModel semanticModel,
        Compilation compilation,
        CancellationToken cancellationToken,
        CallbackDiagnosticBudget diagnosticBudget,
        HashSet<string> handledTypes,
        Location diagnosticLocation,
        System.Action<Diagnostic> reportDiagnostic)
    {
        foreach (var exceptionType in exceptionTypes)
        {
            var typeId = DocumentationCommentId.CreateDeclarationId(exceptionType);
            if (typeId is null || handledTypes.Contains(typeId))
                continue;

            var invocationLocation = use.GetLocation();
            if (!diagnosticBudget.HasCapacity(diagnosticLocation, typeId, outward: false) &&
                !diagnosticBudget.HasCapacity(invocationLocation, typeId, outward: true))
            {
                handledTypes.Add(typeId);
                continue;
            }

            if (
                HasExceptionDocumentation(member, exceptionType, compilation, cancellationToken) ||
                IsCaughtBySurroundingTry(use, exceptionType, semanticModel, compilation, cancellationToken))
                continue;

            var memberName = GetQualifiedMemberName(member);
            var atInvocation = invocationLocation.SourceTree == diagnosticLocation.SourceTree &&
                               invocationLocation.SourceSpan == diagnosticLocation.SourceSpan;
            if (atInvocation)
            {
                var inwardAllowed = diagnosticBudget.TryClaimWarning(diagnosticLocation, typeId, outward: false);
                var outwardAllowed = diagnosticBudget.TryClaimWarning(invocationLocation, typeId, outward: true);
                if (inwardAllowed || outwardAllowed)
                    reportDiagnostic(CreateCallbackDiagnostic(diagnosticLocation, exceptionType, memberName, includeMemberName: false));
            }
            else
            {
                if (diagnosticBudget.TryClaimWarning(diagnosticLocation, typeId, outward: false))
                    reportDiagnostic(CreateCallbackDiagnostic(diagnosticLocation, exceptionType, memberName, includeMemberName: true));
                if (diagnosticBudget.TryClaimWarning(invocationLocation, typeId, outward: true))
                    reportDiagnostic(CreateCallbackDiagnostic(invocationLocation, exceptionType, memberName, includeMemberName: false));
            }
            handledTypes.Add(typeId);
        }
    }

    private static string GetQualifiedMemberName(ISymbol member)
    {
        var containingType = member.ContainingType;
        return containingType is null
            ? member.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            : $"{containingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}.{member.Name}";
    }

    private static Diagnostic CreateCallbackDiagnostic(
        Location location,
        ITypeSymbol exceptionType,
        string memberName,
        bool includeMemberName)
    {
        var properties = ImmutableDictionary<string, string?>.Empty;
        var exceptionTypeId = DocumentationCommentId.CreateDeclarationId(exceptionType);
        if (exceptionTypeId is not null)
            properties = properties.Add(ExceptionTypePropertyKey, exceptionTypeId);

        var memberQualifier = includeMemberName ? $" in '{memberName}'" : string.Empty;
        return Diagnostic.Create(CallbackRule, location, properties, exceptionType.ToDisplayString(), memberQualifier);
    }

    private static ISymbol? GetContainingDocumentedMember(SyntaxNode node, SyntaxNodeAnalysisContext context)
    {
        return GetContainingDocumentedMember(node, context.SemanticModel, context.CancellationToken);
    }

    private static ISymbol? GetContainingDocumentedMember(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var member = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
        while (member is not null && member.Kind != SymbolKind.Method && member.Kind != SymbolKind.Property &&
               member.Kind != SymbolKind.Event && member.Kind != SymbolKind.NamedType)
            member = member.ContainingSymbol;
        return member;
    }

    private static ImmutableArray<ITypeSymbol> GetDocumentedExceptions(ISymbol member, SyntaxNodeAnalysisContext context)
    {
        return GetDocumentedExceptions(member, context.Compilation, context.CancellationToken);
    }

    private static ImmutableArray<ITypeSymbol> GetDocumentedExceptions(ISymbol member, Compilation compilation, CancellationToken cancellationToken)
    {
        var documentation = member.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(documentation))
            return ImmutableArray<ITypeSymbol>.Empty;

        try
        {
            var xml = XDocument.Parse(documentation);
            return xml.Descendants("exception")
                .Select(element => (string?)element.Attribute("cref"))
                .Where(cref => cref is not null)
                .Select(cref => DocumentationCommentId.GetFirstSymbolForDeclarationId(cref!, compilation) as ITypeSymbol)
                .Where(type => type is not null && IsExceptionType(type, compilation))
                .Cast<ITypeSymbol>()
                .GroupBy(type => DocumentationCommentId.CreateDeclarationId(type), System.StringComparer.Ordinal)
                .Select(group => group.First())
                .ToImmutableArray();
        }
        catch (System.Xml.XmlException)
        {
            return ImmutableArray<ITypeSymbol>.Empty;
        }
    }

    private static bool IsCaughtBySurroundingTry(SyntaxNode node, ITypeSymbol exceptionType, SyntaxNodeAnalysisContext context)
    {
        return IsCaughtBySurroundingTry(node, exceptionType, context.SemanticModel, context.Compilation, context.CancellationToken);
    }

    private static bool IsCaughtBySurroundingTry(
        SyntaxNode node,
        ITypeSymbol exceptionType,
        SemanticModel semanticModel,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                return false;

            if (ancestor is not TryStatementSyntax tryStatement || !tryStatement.Block.Span.Contains(node.Span))
                continue;

            foreach (var catchClause in tryStatement.Catches)
            {
                if (catchClause.Filter is not null)
                    continue; // A filter may evaluate to false, so the exception can still escape.

                var caughtType = catchClause.Declaration is null
                    ? compilation.GetTypeByMetadataName("System.Exception")
                    : semanticModel.GetTypeInfo(catchClause.Declaration.Type, cancellationToken).Type;
                if (caughtType is not null && IsSameOrDerivedFrom(exceptionType, caughtType))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSameOrDerivedFrom(ITypeSymbol type, ITypeSymbol baseType)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        return false;
    }

    private static bool IsExceptionType(ITypeSymbol type, Compilation compilation)
    {
        var exceptionType = compilation.GetTypeByMetadataName("System.Exception");
        return exceptionType is not null && IsSameOrDerivedFrom(type, exceptionType);
    }

    private static bool HasExceptionDocumentation(ISymbol member, ITypeSymbol exceptionType, SyntaxNodeAnalysisContext context)
    {
        return GetDocumentedExceptions(member, context)
            .Any(documentedType => SymbolEqualityComparer.Default.Equals(documentedType, exceptionType));
    }

    private static bool HasExceptionDocumentation(ISymbol member, ITypeSymbol exceptionType, Compilation compilation, CancellationToken cancellationToken)
    {
        return GetDocumentedExceptions(member, compilation, cancellationToken)
            .Any(documentedType => SymbolEqualityComparer.Default.Equals(documentedType, exceptionType));
    }

    private static ITypeSymbol? GetRethrownType(
        ThrowStatementSyntax statement,
        SemanticModel semanticModel,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var catchClause = statement.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        if (catchClause is null)
            return null;

        return catchClause.Declaration is null
            ? compilation.GetTypeByMetadataName("System.Exception")
            : semanticModel.GetTypeInfo(catchClause.Declaration.Type, cancellationToken).Type;
    }

    private static bool IsInsideAnonymousFunction(SyntaxNode node) =>
        node.Ancestors().Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax);

    private static Diagnostic CreateDiagnostic(DiagnosticDescriptor rule, Location location, ITypeSymbol exceptionType)
    {
        var properties = ImmutableDictionary<string, string?>.Empty;
        var exceptionTypeId = DocumentationCommentId.CreateDeclarationId(exceptionType);
        if (exceptionTypeId is not null)
            properties = properties.Add(ExceptionTypePropertyKey, exceptionTypeId);

        return Diagnostic.Create(
            rule,
            location,
            properties,
            exceptionType.ToDisplayString());
    }
}
