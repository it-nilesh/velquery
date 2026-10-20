using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using VelQuery.Generators.Infrastructure;
using VelQuery.MongoDB.Generators.Diagnostics;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators.Analysis;

/// <summary>Turns a MongoDB contract class into an equatable <see cref="ContractResult"/>.</summary>
internal static class ContractParser
{
    // VelQuery.QueryCardinality values, as declared in VelQuery.Abstractions.
    private const int CardinalityDefault = 0;
    private const int CardinalityMany = 1;
    private const int CardinalityFirst = 2;
    private const int CardinalityFirstOrDefault = 3;
    private const int CardinalitySingle = 4;
    private const int CardinalitySingleOrDefault = 5;

    private static readonly string[] CardinalityNames =
        ["Default", "Many", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Scalar", "NonQuery"];

    public static ContractResult Parse(GeneratorAttributeSyntaxContext context, ContractKind kind, CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticInfo>();
        try
        {
            var (model, plans) = ParseCore(context, kind, diagnostics, cancellationToken);
            return new ContractResult(model, model is null ? EquatableArray<DocumentPlan>.Empty : plans, diagnostics.ToEquatableArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A generator must never crash the compilation (PRD §11.1); report instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var location = LocationInfo.From((context.TargetNode as ClassDeclarationSyntax)?.Identifier.GetLocation() ?? context.TargetNode.GetLocation());
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.GeneratorFailure, location, context.TargetSymbol.Name, ex.GetType().Name, ex.Message));
            return new ContractResult(null, EquatableArray<DocumentPlan>.Empty, diagnostics.ToEquatableArray());
        }
    }

    private static (ContractModel? Model, EquatableArray<DocumentPlan> Plans) ParseCore(GeneratorAttributeSyntaxContext context, ContractKind kind,
        List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        var none = (default(ContractModel), EquatableArray<DocumentPlan>.Empty);
        if (context.TargetSymbol is not INamedTypeSymbol type || context.TargetNode is not ClassDeclarationSyntax classSyntax)
        {
            return none;
        }

        var classLocation = LocationInfo.From(classSyntax.Identifier.GetLocation());
        var attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Any(a => a.Kind == TypedConstantKind.Error))
        {
            return none; // The compiler reports the malformed attribute.
        }

        var attributeSyntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        var valid = true;
        void Invalid(LocationInfo? location, string message)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.InvalidContract, location ?? classLocation, type.Name, message));
            valid = false;
        }

        // ---- Attribute arguments ----
        string? Positional(int index) => attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value as string : null;
        var queryId = Positional(0);
        var collection = Positional(1);
        string? filter = null;
        string? update = null;
        string? pipeline = null;
        switch (kind)
        {
            case ContractKind.Find or ContractKind.Count or ContractKind.Delete:
                filter = Positional(2);
                break;
            case ContractKind.Aggregate:
                pipeline = Positional(2);
                break;
            case ContractKind.Update:
                filter = Positional(2);
                update = Positional(3);
                break;
        }

        string? sort = null;
        string? projection = null;
        int limit = 0, skip = 0, timeout = 0, cardinality = CardinalityDefault;
        bool many = false, upsert = false, allowDiskUse = false;
        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Sort": sort = named.Value.Value as string; break;
                case "Projection": projection = named.Value.Value as string; break;
                case "Limit" when named.Value.Value is int value: limit = value; break;
                case "Skip" when named.Value.Value is int value: skip = value; break;
                case "TimeoutMilliseconds" when named.Value.Value is int value: timeout = value; break;
                case "Cardinality" when named.Value.Value is int value: cardinality = value; break;
                case "Many" when named.Value.Value is bool value: many = value; break;
                case "Upsert" when named.Value.Value is bool value: upsert = value; break;
                case "AllowDiskUse" when named.Value.Value is bool value: allowDiskUse = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(queryId))
        {
            Invalid(LocationInfo.From(FindArgument(attributeSyntax, 0, "queryId")), "queryId must not be empty");
        }

        if (string.IsNullOrWhiteSpace(collection) || collection!.IndexOf('$') >= 0 || collection.IndexOf('\0') >= 0 || collection.StartsWith("system.", StringComparison.Ordinal))
        {
            Invalid(LocationInfo.From(FindArgument(attributeSyntax, 1, "collection")),
                "collection must be a non-empty collection name without '$' or NUL that does not start with 'system.'");
        }

        foreach (var (name, value) in new[] { ("Limit", limit), ("Skip", skip), ("TimeoutMilliseconds", timeout) })
        {
            if (value < 0)
            {
                Invalid(LocationInfo.From(FindNamedArgument(attributeSyntax, name)), $"{name} must not be negative");
            }
        }

        if (cardinality is < CardinalityDefault or > CardinalitySingleOrDefault)
        {
            Invalid(LocationInfo.From(FindNamedArgument(attributeSyntax, "Cardinality")),
                $"Cardinality {(cardinality >= 0 && cardinality < CardinalityNames.Length ? CardinalityNames[cardinality] : cardinality.ToString(CultureInfo.InvariantCulture))} is not supported for MongoDB contracts; use Default, Many, First, FirstOrDefault, Single or SingleOrDefault");
            cardinality = CardinalityDefault;
        }

        // ---- Containing type shape ----
        if (type.IsGenericType || ContainingTypes(type).Any(t => t.IsGenericType))
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.ContractTypeNotPartial, classLocation, type.Name, "must not be generic or nested in a generic type"));
            valid = false;
        }

        foreach (var t in new[] { type }.Concat(ContainingTypes(type)))
        {
            if (!t.DeclaringSyntaxReferences.Any() || !t.DeclaringSyntaxReferences.All(r => r.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                var detail = SymbolEqualityComparer.Default.Equals(t, type)
                    ? "must be declared partial"
                    : $"must be declared in partial types, but containing type '{t.Name}' is not partial";
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.ContractTypeNotPartial, classLocation, type.Name, detail));
                return none;
            }
        }

        // ---- The partial method ----
        var methods = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.IsPartialDefinition && m.PartialImplementationPart is null)
            .ToList();
        if (methods.Count != 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.ContractMethodCount, classLocation, type.Name, methods.Count.ToString(CultureInfo.InvariantCulture)));
            return none;
        }

        var method = methods[0];
        var methodSyntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as MethodDeclarationSyntax;
        var methodLocation = LocationInfo.From(methodSyntax?.Identifier.GetLocation()) ?? classLocation;
        var returnLocation = LocationInfo.From(methodSyntax?.ReturnType) ?? methodLocation;
        var signatureValid = true;
        void InvalidSignature(LocationInfo? location, string message)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.InvalidMethodSignature, location ?? methodLocation, method.Name, message));
            valid = false;
            signatureValid = false;
        }

        if (method.IsGenericMethod)
        {
            InvalidSignature(methodLocation, "must not be generic");
        }

        if (methodSyntax?.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword) || m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)) != true)
        {
            InvalidSignature(methodLocation, "must declare an explicit accessibility (for example 'public static partial')");
        }

        if (method.IsVirtual || method.IsOverride || method.IsAbstract || method.IsExtern)
        {
            InvalidSignature(methodLocation, "must not be virtual, override, abstract or extern");
        }

        string? sessionName = null;
        string? tokenName = null;
        var others = new List<IParameterSymbol>();
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            var location = LocationInfo.From(parameter.Locations.FirstOrDefault());
            if (parameter.RefKind != RefKind.None || parameter.IsParams || parameter.IsThis)
            {
                InvalidSignature(location, $"parameter '{parameter.Name}' must not be ref, in, out, params or this");
            }
            else if (MongoTypeFacts.IsType(parameter.Type, "VelQuery.MongoDB", "VelQueryMongoSession"))
            {
                if (i != 0)
                {
                    InvalidSignature(location, "must declare the VelQueryMongoSession as its first parameter");
                }

                sessionName = parameter.Name;
            }
            else if (MongoTypeFacts.IsType(parameter.Type, "System.Threading", "CancellationToken"))
            {
                if (tokenName is not null || i != method.Parameters.Length - 1)
                {
                    InvalidSignature(location, "must declare a single CancellationToken as its last parameter");
                }

                tokenName = parameter.Name;
            }
            else
            {
                others.Add(parameter);
            }
        }

        if (sessionName is null)
        {
            InvalidSignature(methodLocation, "must take a VelQueryMongoSession as its first parameter");
        }

        // ---- Operation and document type from the return type ----
        var plans = new DocumentPlanBuilder(diagnostics, returnLocation);
        var (operation, documentType) = ResolveOperation(kind, method, cardinality, returnLocation, diagnostics);
        string? documentParameter = null;
        if (kind == ContractKind.Insert && operation is not null)
        {
            (operation, documentType, documentParameter) = ResolveInsert(method, others, returnLocation, methodLocation, diagnostics);
            others.Clear();
        }

        if (operation is null)
        {
            valid = false;
        }

        string? serializer = null;
        if (documentType is not null)
        {
            serializer = plans.Build(documentType);
            if (serializer is null)
            {
                valid = false;
            }
        }

        // ---- Parameters and placeholders (skipped when the signature is broken, to avoid cascades) ----
        var parameters = new Dictionary<string, BindableParameter>(StringComparer.OrdinalIgnoreCase);
        var arguments = new List<ArgumentModel>();
        if (signatureValid)
        {
            foreach (var parameter in others)
            {
                if (parameters.TryGetValue(parameter.Name, out var previous))
                {
                    diagnostics.Add(DiagnosticInfo.Create(Descriptors.DuplicateParameter, LocationInfo.From(parameter.Locations.FirstOrDefault()),
                        previous.Symbol.Name, parameter.Name, parameter.Name));
                    valid = false;
                    continue;
                }

                var (shape, reason) = MongoTypeFacts.ParameterShape(parameter.Type);
                var field = MongoTypeFacts.Identifier(parameter.Name);
                parameters.Add(parameter.Name, new BindableParameter(parameter, field, shape, reason));
                arguments.Add(new ArgumentModel(field, MongoTypeFacts.Identifier(parameter.Name), MongoTypeFacts.Display(parameter.Type)));
            }
        }

        var methodName = method.Name;
        string? Document(string? json, string argumentName, int position, string label, Func<BsonEmitter, Json.JsonNode, string?> emit)
        {
            if (json is null)
            {
                return null;
            }

            var syntax = position >= 0 ? FindArgument(attributeSyntax, position, argumentName) : FindNamedArgument(attributeSyntax, argumentName);
            var emitter = new BsonEmitter(parameters, diagnostics, Locator(syntax, json, classLocation), label, queryId ?? string.Empty, methodName);
            var node = emitter.Parse(json);
            var result = node is null ? null : emit(emitter, node);
            if (result is null || emitter.HasErrors)
            {
                valid = false;
                return null;
            }

            return result;
        }

        string? filterExpression = null, updateExpression = null, sortExpression = null, projectionExpression = null;
        var stages = new List<string>();
        if (signatureValid)
        {
            filterExpression = Document(filter, "filter", 2, "Filter", static (e, n) => e.Filter(n));
            updateExpression = Document(update, "update", 3, "Update", static (e, n) => e.Update(n));
            sortExpression = Document(sort, "Sort", -1, "Sort", static (e, n) => e.Constant(n, "a sort document"));
            projectionExpression = Document(projection, "Projection", -1, "Projection", static (e, n) => e.Constant(n, "a projection document"));
            if (pipeline is not null)
            {
                var syntax = FindArgument(attributeSyntax, 2, "pipeline");
                var emitter = new BsonEmitter(parameters, diagnostics, Locator(syntax, pipeline, classLocation), "Pipeline", queryId ?? string.Empty, methodName);
                var node = emitter.Parse(pipeline);
                var emitted = node is null ? null : emitter.Pipeline(node, allowWriteStages: false);
                if (emitted is null || emitter.HasErrors)
                {
                    valid = false;
                }
                else
                {
                    stages = emitted;
                }
            }

            if (kind is not ContractKind.Insert && filter is null && pipeline is null)
            {
                Invalid(classLocation, "the filter must not be null");
            }

            foreach (var parameter in parameters.Values)
            {
                if (!parameter.Used)
                {
                    diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnusedParameter, LocationInfo.From(parameter.Symbol.Locations.FirstOrDefault()),
                        parameter.Symbol.Name, methodName, queryId ?? string.Empty));
                }
            }
        }

        if (!valid || operation is null || sessionName is null || queryId is null || collection is null)
        {
            return none;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var model = new ContractModel(
            HintName: HintName(type),
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            ContainingTypes: ContainingTypes(type).Reverse().Select(Declaration).ToEquatableArray(),
            ContractType: Declaration(type),
            Kind: kind,
            Operation: operation.Value,
            QueryId: queryId,
            Collection: collection,
            DocumentType: documentType is null ? null : MongoTypeFacts.DisplayNoAnnotations(documentType),
            Serializer: serializer,
            Arguments: arguments.ToEquatableArray(),
            FilterExpression: filterExpression,
            UpdateExpression: updateExpression,
            SortExpression: sortExpression,
            ProjectionExpression: projectionExpression,
            PipelineStages: stages.ToEquatableArray(),
            Limit: limit,
            Skip: skip,
            Many: many,
            Upsert: upsert,
            AllowDiskUse: allowDiskUse,
            TimeoutMilliseconds: timeout,
            DocumentParameter: documentParameter,
            Method: new MethodModel(
                AccessibilityText(method.DeclaredAccessibility),
                method.IsStatic,
                MongoTypeFacts.Identifier(method.Name),
                MongoTypeFacts.Display(method.ReturnType),
                method.Parameters.Select(p => new MethodParameterModel(MongoTypeFacts.Identifier(p.Name), MongoTypeFacts.Display(p.Type))).ToEquatableArray(),
                MongoTypeFacts.Identifier(sessionName),
                tokenName is null ? null : MongoTypeFacts.Identifier(tokenName)));
        return (model, plans.Plans);
    }

    private static (Operation? Operation, ITypeSymbol? DocumentType) ResolveOperation(ContractKind kind, IMethodSymbol method, int cardinality,
        LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        var returnType = method.ReturnType;
        if (returnType.TypeKind == TypeKind.Error)
        {
            return (null, null);
        }

        (Operation?, ITypeSymbol?) Unsupported(string reason)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedReturnType, location, method.Name, MongoTypeFacts.Display(returnType), OperationName(kind, cardinality), reason));
            return (null, null);
        }

        var task = returnType as INamedTypeSymbol;
        var taskResult = task is not null && MongoTypeFacts.IsType(task, "System.Threading.Tasks", "Task", 1) ? task.TypeArguments[0] : null;
        switch (kind)
        {
            case ContractKind.Count or ContractKind.Delete:
                return taskResult?.SpecialType == SpecialType.System_Int64
                    ? (kind == ContractKind.Count ? Operation.Count : Operation.Delete, null)
                    : Unsupported($"{(kind == ContractKind.Count ? "a count" : "a delete")} returns the count the server reports as Task<long>");
            case ContractKind.Update:
                return taskResult is not null && MongoTypeFacts.IsType(taskResult, "VelQuery.MongoDB", "VelQueryMongoUpdateResult")
                    ? (Operation.Update, null)
                    : Unsupported("an update returns the server's matched and modified counts as Task<VelQueryMongoUpdateResult>");
            case ContractKind.Insert:
                return (Operation.InsertOne, null); // Refined by ResolveInsert.
        }

        // Find and aggregate.
        if (task is not null && MongoTypeFacts.IsType(task, "System.Collections.Generic", "IAsyncEnumerable", 1))
        {
            return cardinality is CardinalityDefault or CardinalityMany
                ? (Operation.Stream, Unannotated(task.TypeArguments[0]))
                : Unsupported("IAsyncEnumerable<T> streams every document; use Default or Many");
        }

        if (taskResult is null)
        {
            return Unsupported("contract methods return Task<IReadOnlyList<T>>, IAsyncEnumerable<T>, Task<T> or Task<T?>");
        }

        if (taskResult.TypeKind == TypeKind.Error)
        {
            return (null, null);
        }

        if (taskResult is INamedTypeSymbol list && MongoTypeFacts.IsType(list, "System.Collections.Generic", "IReadOnlyList", 1))
        {
            return cardinality is CardinalityDefault or CardinalityMany
                ? (Operation.List, Unannotated(list.TypeArguments[0]))
                : Unsupported("Task<IReadOnlyList<T>> buffers every document; use Default or Many");
        }

        if (taskResult.IsValueType)
        {
            return Unsupported("single-document results must be classes or records (OrDefault operations return null when nothing matches)");
        }

        var nullable = taskResult.NullableAnnotation == NullableAnnotation.Annotated;
        switch (cardinality)
        {
            case CardinalityMany:
                return Unsupported("Many requires Task<IReadOnlyList<T>> or IAsyncEnumerable<T>");
            case CardinalityFirst:
                return (Operation.First, Unannotated(taskResult));
            case CardinalitySingle:
                return (Operation.Single, Unannotated(taskResult));
            case CardinalityFirstOrDefault or CardinalitySingleOrDefault:
                if (!nullable)
                {
                    return Unsupported("an OrDefault cardinality returns null when nothing matches, so the result type must be nullable (T?)");
                }

                return (cardinality == CardinalityFirstOrDefault ? Operation.FirstOrDefault : Operation.SingleOrDefault, Unannotated(taskResult));
            default:
                if (taskResult.NullableAnnotation == NullableAnnotation.None)
                {
                    return Unsupported("the result type is declared in a nullable-oblivious context, so 'no document' handling cannot be inferred; enable nullable or set Cardinality");
                }

                return (nullable ? Operation.SingleOrDefault : Operation.Single, Unannotated(taskResult));
        }
    }

    private static (Operation?, ITypeSymbol?, string?) ResolveInsert(IMethodSymbol method, List<IParameterSymbol> others, LocationInfo? returnLocation,
        LocationInfo? methodLocation, List<DiagnosticInfo> diagnostics)
    {
        if (others.Count != 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.InvalidMethodSignature, methodLocation, method.Name,
                $"must declare exactly one document (or sequence of documents) parameter between the session and the optional CancellationToken, but declares {others.Count}"));
            return (null, null, null);
        }

        var parameter = others[0];
        var element = MongoTypeFacts.SequenceElement(parameter.Type);
        var returnType = method.ReturnType as INamedTypeSymbol;
        if (element is not null)
        {
            if (returnType is null || !MongoTypeFacts.IsType(returnType, "System.Threading.Tasks", "Task", 1) || returnType.TypeArguments[0].SpecialType != SpecialType.System_Int64)
            {
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedReturnType, returnLocation, method.Name, MongoTypeFacts.Display(method.ReturnType),
                    "a bulk insert", "a bulk insert returns the inserted count the server reports as Task<long>"));
                return (null, null, null);
            }

            return (Operation.InsertMany, Unannotated(element), MongoTypeFacts.Identifier(parameter.Name));
        }

        if (returnType is null || !MongoTypeFacts.IsType(returnType, "System.Threading.Tasks", "Task"))
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedReturnType, returnLocation, method.Name, MongoTypeFacts.Display(method.ReturnType),
                "an insert", "inserting one document returns Task; take an IEnumerable<T> and return Task<long> for a bulk insert"));
            return (null, null, null);
        }

        return (Operation.InsertOne, Unannotated(parameter.Type), MongoTypeFacts.Identifier(parameter.Name));
    }

    private static ITypeSymbol Unannotated(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

    private static string OperationName(ContractKind kind, int cardinality) => kind switch
    {
        ContractKind.Find or ContractKind.Aggregate => $"{kind.ToString().ToLowerInvariant()} with Cardinality = {(cardinality >= 0 && cardinality < CardinalityNames.Length ? CardinalityNames[cardinality] : "?")}",
        _ => "a " + kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Maps a character range in the JSON value to a source location inside the string literal (regular strings with
    /// escapes, verbatim strings and single- or multi-line raw strings); otherwise the whole argument.
    /// </summary>
    private static Func<int, int, LocationInfo?> Locator(ExpressionSyntax? argument, string value, LocationInfo? fallback)
    {
        var whole = LocationInfo.From(argument) ?? fallback;
        if (argument is not LiteralExpressionSyntax literal || literal.SyntaxTree is null || !literal.Token.IsKind(SyntaxKind.StringLiteralToken)
            && !literal.Token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) && !literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
        {
            return (_, _) => whole;
        }

        var map = SourceMap.Build(literal.Token.Text, value);
        if (map is null)
        {
            return (_, _) => whole;
        }

        var start = literal.Token.SpanStart;
        var tree = literal.SyntaxTree;
        return (position, length) =>
        {
            position = Math.Max(0, Math.Min(position, value.Length));
            var end = Math.Max(position, Math.Min(position + Math.Max(length, 0), value.Length));
            var textStart = map[position];
            var textEnd = end > position ? map[end - 1] + 1 : textStart;
            return LocationInfo.From(tree, new TextSpan(start + textStart, Math.Max(0, textEnd - textStart)));
        };
    }

    private static ExpressionSyntax? FindArgument(AttributeSyntax? attribute, int position, string name)
    {
        var arguments = attribute?.ArgumentList?.Arguments;
        if (arguments is null)
        {
            return null;
        }

        var positional = 0;
        foreach (var argument in arguments)
        {
            if (argument.NameEquals is not null)
            {
                continue;
            }

            if (argument.NameColon is not null)
            {
                if (argument.NameColon.Name.Identifier.ValueText == name)
                {
                    return argument.Expression;
                }

                continue;
            }

            if (positional++ == position)
            {
                return argument.Expression;
            }
        }

        return null;
    }

    private static ExpressionSyntax? FindNamedArgument(AttributeSyntax? attribute, string name)
        => attribute?.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)?.Expression;

    private static IEnumerable<INamedTypeSymbol> ContainingTypes(INamedTypeSymbol type)
    {
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
        {
            yield return t;
        }
    }

    private static TypeDeclarationModel Declaration(INamedTypeSymbol type)
    {
        var keyword = type switch
        {
            { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
            { IsRecord: true } => "record",
            { TypeKind: TypeKind.Struct } => "struct",
            { TypeKind: TypeKind.Interface } => "interface",
            _ => "class",
        };
        return new TypeDeclarationModel(keyword, MongoTypeFacts.Identifier(type.Name), type.IsStatic);
    }

    private static string HintName(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        return name.Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_') + ".Mongo.g.cs";
    }

    private static string AccessibilityText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "private",
    };
}
