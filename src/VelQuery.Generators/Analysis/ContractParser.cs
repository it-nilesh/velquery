using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using VelQuery.Generators.Diagnostics;
using VelQuery.Generators.Infrastructure;
using VelQuery.Generators.Model;
using VelQuery.Generators.Sql;

namespace VelQuery.Generators.Analysis;

/// <summary>Turns a <c>[VelQuerySql]</c> class into an equatable <see cref="ContractResult"/>.</summary>
internal static class ContractParser
{
    // QueryCardinality and CommandType values, as declared in VelQuery.Abstractions and System.Data.
    private const int CardinalityDefault = 0;
    private const int CardinalityMany = 1;
    private const int CardinalityFirst = 2;
    private const int CardinalityFirstOrDefault = 3;
    private const int CardinalitySingle = 4;
    private const int CardinalitySingleOrDefault = 5;
    private const int CardinalityScalar = 6;
    private const int CardinalityNonQuery = 7;
    private const int CommandTypeText = 1;
    private const int CommandTypeStoredProcedure = 4;

    private static readonly string[] CardinalityNames =
        ["Default", "Many", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Scalar", "NonQuery"];

    public static ContractResult Parse(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticInfo>();
        try
        {
            var (model, row) = ParseCore(context, diagnostics, cancellationToken);
            return new ContractResult(model, row, diagnostics.ToEquatableArray());
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
            return new ContractResult(null, null, diagnostics.ToEquatableArray());
        }
    }

    private static (ContractModel? Model, RowPlan? Row) ParseCore(GeneratorAttributeSyntaxContext context, List<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type || context.TargetNode is not ClassDeclarationSyntax classSyntax)
        {
            return (null, null);
        }

        var classLocation = LocationInfo.From(classSyntax.Identifier.GetLocation());
        var attribute = context.Attributes[0];
        if (attribute.ConstructorArguments.Length != 2 || attribute.ConstructorArguments.Any(a => a.Kind == TypedConstantKind.Error))
        {
            return (null, null); // The compiler reports the malformed attribute.
        }

        var attributeSyntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        var sqlArgument = FindArgument(attributeSyntax, 1, "sql");
        var sqlLocation = LocationInfo.From(sqlArgument) ?? LocationInfo.From(attributeSyntax) ?? classLocation;

        // ---- Attribute arguments ----
        var queryId = attribute.ConstructorArguments[0].Value as string;
        var sql = attribute.ConstructorArguments[1].Value as string;
        var cardinality = CardinalityDefault;
        var commandType = CommandTypeText;
        var timeout = 0;
        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Cardinality" when named.Value.Value is int value:
                    cardinality = value;
                    break;
                case "CommandType" when named.Value.Value is int value:
                    commandType = value;
                    break;
                case "CommandTimeoutSeconds" when named.Value.Value is int value:
                    timeout = value;
                    break;
            }
        }

        var valid = true;
        void Invalid(LocationInfo? location, string message)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.InvalidAttributeArgument, location, type.Name, message));
            valid = false;
        }

        if (string.IsNullOrWhiteSpace(queryId))
        {
            Invalid(LocationInfo.From(FindArgument(attributeSyntax, 0, "queryId")) ?? classLocation, "queryId must not be empty");
        }

        if (sql is null || string.IsNullOrWhiteSpace(sql))
        {
            Invalid(sqlLocation, "sql must not be empty");
        }

        if (cardinality is < CardinalityDefault or > CardinalityNonQuery)
        {
            Invalid(LocationInfo.From(FindNamedArgument(attributeSyntax, "Cardinality")) ?? classLocation, $"Cardinality value {cardinality} is not defined");
        }

        if (commandType is not (CommandTypeText or CommandTypeStoredProcedure))
        {
            Invalid(LocationInfo.From(FindNamedArgument(attributeSyntax, "CommandType")) ?? classLocation, "CommandType must be Text or StoredProcedure");
        }

        if (timeout < 0)
        {
            Invalid(LocationInfo.From(FindNamedArgument(attributeSyntax, "CommandTimeoutSeconds")) ?? classLocation, "CommandTimeoutSeconds must not be negative");
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
                return (null, null); // Method diagnostics would only cascade from this one.
            }
        }

        // ---- The partial method ----
        var methods = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.IsPartialDefinition && m.PartialImplementationPart is null)
            .ToList();
        if (methods.Count != 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.ContractMethodCount, classLocation, type.Name, methods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return (null, null);
        }

        var method = methods[0];
        var methodSyntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as MethodDeclarationSyntax;
        var methodLocation = LocationInfo.From(methodSyntax?.Identifier.GetLocation()) ?? classLocation;
        var returnLocation = LocationInfo.From(methodSyntax?.ReturnType) ?? methodLocation;

        void InvalidSignature(LocationInfo? location, string message)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.InvalidMethodSignature, location ?? methodLocation, method.Name, message));
            valid = false;
        }

        if (method.IsGenericMethod)
        {
            InvalidSignature(methodLocation, "must not be generic");
        }

        if (method.DeclaredAccessibility == Accessibility.NotApplicable || methodSyntax?.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword) || m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)) != true)
        {
            InvalidSignature(methodLocation, "must declare an explicit accessibility (for example 'public static partial')");
        }

        if (method.IsVirtual || method.IsOverride || method.IsAbstract || method.IsExtern)
        {
            InvalidSignature(methodLocation, "must not be virtual, override, abstract or extern");
        }

        var parameters = method.Parameters;
        var signatureValid = true;
        void InvalidParameter(IParameterSymbol parameter, string message)
        {
            InvalidSignature(LocationInfo.From(parameter.Locations.FirstOrDefault()), message);
            signatureValid = false;
        }

        string? sessionName = null;
        string? tokenName = null;
        var bindable = new List<IParameterSymbol>();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.RefKind != RefKind.None || parameter.IsParams || parameter.IsThis)
            {
                InvalidParameter(parameter, $"parameter '{parameter.Name}' must not be ref, in, out, params or this");
            }
            else if (IsSession(parameter.Type))
            {
                if (sessionName is not null)
                {
                    InvalidParameter(parameter, "must declare exactly one session parameter");
                }
                else if (i != 0)
                {
                    InvalidParameter(parameter, "must declare the session as its first parameter");
                }

                sessionName = parameter.Name;
            }
            else if (TypeFacts.IsType(parameter.Type, "System.Threading", "CancellationToken"))
            {
                if (tokenName is not null || i != parameters.Length - 1)
                {
                    InvalidParameter(parameter, "must declare a single CancellationToken as its last parameter");
                }

                tokenName = parameter.Name;
            }
            else
            {
                bindable.Add(parameter);
            }
        }

        if (sessionName is null)
        {
            InvalidSignature(methodLocation, "must take an IVelQuerySession (or VelQuerySession) as its first parameter");
            signatureValid = false;
        }

        // ---- Operation from return type and cardinality ----
        var (operation, rowType) = ResolveOperation(method, cardinality, returnLocation, diagnostics);
        if (operation is null)
        {
            valid = false;
        }

        // ---- Row mapping ----
        RowPlan? rowPlan = null;
        string? rowTypeDisplay = null;
        string? rowFactory = null;
        if (operation is not null and not QueryOperation.Execute && rowType is not null)
        {
            var descriptorRow = rowType;
            if (operation is QueryOperation.FirstOrDefault or QueryOperation.SingleOrDefault && !rowType.IsValueType)
            {
                // QueryXxxOrDefaultAsync<TArgs, TRow> returns Task<TRow?>; bind TRow to the non-annotated type.
                descriptorRow = rowType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            }

            rowTypeDisplay = TypeFacts.Display(descriptorRow);
            if (TypeFacts.IsScalar(rowType))
            {
                rowFactory = "global::VelQuery.Mapping.ScalarRowMapperFactory<" + rowTypeDisplay + ">.Instance";
            }
            else if (operation == QueryOperation.Scalar)
            {
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedReturnType, returnLocation, method.Name, TypeFacts.Display(method.ReturnType), "Scalar", "Scalar requires a scalar result type such as long, string or an enum"));
                valid = false;
            }
            else
            {
                rowPlan = RowPlanBuilder.Build(rowType.WithNullableAnnotation(NullableAnnotation.NotAnnotated), returnLocation, diagnostics);
                if (rowPlan is null)
                {
                    valid = false;
                }
                else
                {
                    rowFactory = "global::VelQuery.Generated." + rowPlan.FactoryName + ".Instance";
                }
            }
        }

        // ---- Parameters and SQL placeholders (skipped when the signature is broken, to avoid cascades) ----
        var bindings = !signatureValid ? new List<BindingModel>() : BindParameters(method, bindable, sql ?? string.Empty, queryId ?? string.Empty, commandType, sqlArgument, sqlLocation, diagnostics, ref valid);

        if (!valid || operation is null || sessionName is null || queryId is null || sql is null)
        {
            return (null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var model = new ContractModel(
            HintName: HintName(type),
            Namespace: type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            ContainingTypes: ContainingTypes(type).Reverse().Select(Declaration).ToEquatableArray(),
            ContractType: Declaration(type),
            QueryId: queryId,
            Sql: sql,
            CommandType: commandType == CommandTypeStoredProcedure ? "StoredProcedure" : "Text",
            TimeoutSeconds: timeout,
            Operation: operation.Value,
            RowType: rowTypeDisplay,
            RowFactory: rowFactory,
            Method: new MethodModel(
                AccessibilityText(method.DeclaredAccessibility),
                method.IsStatic,
                TypeFacts.Identifier(method.Name),
                TypeFacts.Display(method.ReturnType),
                parameters.Select(p => new MethodParameterModel(TypeFacts.Identifier(p.Name), TypeFacts.Display(p.Type))).ToEquatableArray(),
                TypeFacts.Identifier(sessionName),
                tokenName is null ? null : TypeFacts.Identifier(tokenName)),
            Bindings: bindings.ToEquatableArray());
        return (model, rowPlan);
    }

    private static (QueryOperation? Operation, ITypeSymbol? RowType) ResolveOperation(IMethodSymbol method, int cardinality, LocationInfo? location, List<DiagnosticInfo> diagnostics)
    {
        var returnType = method.ReturnType;
        var cardinalityName = cardinality >= 0 && cardinality < CardinalityNames.Length ? CardinalityNames[cardinality] : cardinality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (QueryOperation?, ITypeSymbol?) Unsupported(string reason)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedReturnType, location, method.Name, TypeFacts.Display(returnType), cardinalityName, reason));
            return (null, null);
        }

        (QueryOperation?, ITypeSymbol?) Ambiguous(string reason)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.AmbiguousOperation, location, method.Name, TypeFacts.Display(returnType), reason));
            return (null, null);
        }

        if (returnType.TypeKind == TypeKind.Error)
        {
            return (null, null);
        }

        if (cardinality is < CardinalityDefault or > CardinalityNonQuery)
        {
            return (null, null); // Reported as VQ1017.
        }

        if (returnType is INamedTypeSymbol stream && TypeFacts.IsType(stream, "System.Collections.Generic", "IAsyncEnumerable", 1))
        {
            return cardinality is CardinalityDefault or CardinalityMany
                ? (QueryOperation.Stream, stream.TypeArguments[0])
                : Unsupported("IAsyncEnumerable<T> streams all rows; use Default or Many");
        }

        if (returnType is not INamedTypeSymbol task || !TypeFacts.IsType(task, "System.Threading.Tasks", "Task", 1))
        {
            return Unsupported("contract methods return Task<T> or IAsyncEnumerable<T>");
        }

        var result = task.TypeArguments[0];
        if (result.TypeKind == TypeKind.Error)
        {
            return (null, null);
        }

        if (result is INamedTypeSymbol list && TypeFacts.IsType(list, "System.Collections.Generic", "IReadOnlyList", 1))
        {
            return cardinality is CardinalityDefault or CardinalityMany
                ? (QueryOperation.Query, list.TypeArguments[0])
                : Unsupported("Task<IReadOnlyList<T>> buffers all rows; use Default or Many");
        }

        var isInt = result.SpecialType == SpecialType.System_Int32;
        var nullable = result.IsValueType ? TypeFacts.NullableUnderlying(result) is not null : result.NullableAnnotation == NullableAnnotation.Annotated;
        switch (cardinality)
        {
            case CardinalityNonQuery:
                return isInt ? (QueryOperation.Execute, null) : Unsupported("NonQuery returns the affected row count as Task<int>");
            case CardinalityMany:
                return Unsupported("Many requires Task<IReadOnlyList<T>> or IAsyncEnumerable<T>");
            case CardinalityScalar:
                return (QueryOperation.Scalar, result);
            case CardinalityFirst:
                return (QueryOperation.First, result);
            case CardinalitySingle:
                return (QueryOperation.Single, result);
            case CardinalityFirstOrDefault or CardinalitySingleOrDefault:
                if (!nullable && result.NullableAnnotation != NullableAnnotation.None)
                {
                    return Unsupported("an OrDefault cardinality returns null when there are no rows, so the result type must be nullable (T?)");
                }

                return (cardinality == CardinalityFirstOrDefault ? QueryOperation.FirstOrDefault : QueryOperation.SingleOrDefault, result);
            default:
                if (isInt)
                {
                    return Ambiguous("Task<int> could be an affected-row count (NonQuery) or a one-row integer result (Single or Scalar)");
                }

                if (!result.IsValueType && result.NullableAnnotation == NullableAnnotation.None)
                {
                    return Ambiguous("the result type is declared in a nullable-oblivious context, so 'no rows' handling cannot be inferred");
                }

                return (nullable ? QueryOperation.SingleOrDefault : QueryOperation.Single, result);
        }
    }

    private static List<BindingModel> BindParameters(IMethodSymbol method, List<IParameterSymbol> bindable, string sql, string queryId,
        int commandType, ExpressionSyntax? sqlArgument, LocationInfo? sqlLocation, List<DiagnosticInfo> diagnostics, ref bool valid)
    {
        var candidates = new List<(IParameterSymbol Parameter, string SqlName, ParameterOverrides Overrides, LocationInfo? Location)>();
        var seen = new Dictionary<string, IParameterSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in bindable)
        {
            var overrides = ParameterOverrides.Read(parameter);
            var sqlName = overrides.Name ?? parameter.Name;
            var location = LocationInfo.From(parameter.Locations.FirstOrDefault());
            if (seen.TryGetValue(sqlName, out var previous))
            {
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.DuplicateParameter, location, previous.Name, parameter.Name, sqlName));
                valid = false;
                continue;
            }

            seen.Add(sqlName, parameter);
            if (overrides.DbType is null && TypeFacts.InferDbType(parameter.Type) is null)
            {
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedParameterType, location, parameter.Name, TypeFacts.Display(parameter.Type)));
                valid = false;
            }

            candidates.Add((parameter, sqlName, overrides, location));
        }

        var bindings = new List<BindingModel>();
        if (commandType == CommandTypeStoredProcedure)
        {
            // Stored procedure parameters are not visible in the SQL text: bind every method parameter by name.
            foreach (var candidate in candidates)
            {
                bindings.Add(CreateBinding(candidate.Parameter, candidate.SqlName, candidate.Overrides));
            }

            return bindings;
        }

        var scan = SqlParameterScanner.Scan(sql);
        if (scan.Unterminated is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnterminatedSql, sqlLocation, queryId, scan.Unterminated));
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var placeholder in scan.Placeholders)
        {
            if (!used.Add(placeholder.Name))
            {
                continue;
            }

            var index = candidates.FindIndex(c => string.Equals(c.SqlName, placeholder.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                var location = PlaceholderLocation(sqlArgument, sql, placeholder) ?? sqlLocation;
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnknownSqlParameter, location, queryId, placeholder.Name, method.Name));
                valid = false;
                continue;
            }

            bindings.Add(CreateBinding(candidates[index].Parameter, placeholder.Name, candidates[index].Overrides));
        }

        foreach (var candidate in candidates)
        {
            if (!used.Contains(candidate.SqlName))
            {
                diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnusedParameter, candidate.Location, candidate.Parameter.Name, method.Name, queryId, candidate.SqlName));
            }
        }

        return bindings;
    }

    private static BindingModel CreateBinding(IParameterSymbol parameter, string sqlName, ParameterOverrides overrides)
    {
        var valueType = TypeFacts.NullableUnderlying(parameter.Type) ?? parameter.Type;
        var enumUnderlying = valueType is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }
            ? TypeFacts.DisplayNoAnnotations(underlying)
            : null;
        return new BindingModel(
            FieldName: TypeFacts.Identifier(sqlName),
            ParameterName: TypeFacts.Identifier(parameter.Name),
            SqlName: sqlName,
            Type: TypeFacts.Display(parameter.Type),
            DbType: overrides.DbType ?? TypeFacts.InferDbType(parameter.Type),
            Size: overrides.Size,
            Precision: overrides.Precision,
            Scale: overrides.Scale,
            EnumUnderlyingType: enumUnderlying,
            IsNullableValueType: TypeFacts.NullableUnderlying(parameter.Type) is not null);
    }

    /// <summary>Points at the placeholder inside the string literal when the literal's text maps 1:1 to its value.</summary>
    private static LocationInfo? PlaceholderLocation(ExpressionSyntax? argument, string sql, SqlPlaceholder placeholder)
    {
        if (argument is not LiteralExpressionSyntax literal || literal.SyntaxTree is null)
        {
            return null;
        }

        var text = literal.Token.Text;
        var offset = text.IndexOf(sql, StringComparison.Ordinal);
        if (offset < 0 || text.IndexOf(sql, offset + 1, StringComparison.Ordinal) >= 0)
        {
            return null; // Escapes or raw-string indentation change offsets; fall back to the whole argument.
        }

        return LocationInfo.From(literal.SyntaxTree, new TextSpan(literal.Token.SpanStart + offset + placeholder.Start, placeholder.Length));
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

    private static bool IsSession(ITypeSymbol type)
        => TypeFacts.IsType(type, "VelQuery", "IVelQuerySession") || TypeFacts.IsType(type, "VelQuery", "VelQuerySession");

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
        return new TypeDeclarationModel(keyword, TypeFacts.Identifier(type.Name), type.IsStatic);
    }

    private static string HintName(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        return name.Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_') + ".g.cs";
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

    /// <summary>Explicitly set <c>[VelQueryParameter]</c> properties.</summary>
    private sealed class ParameterOverrides
    {
        private static readonly string[] DbTypeNames =
        [
            "AnsiString", "Binary", "Byte", "Boolean", "Currency", "Date", "DateTime", "Decimal", "Double", "Guid", "Int16", "Int32",
            "Int64", "Object", "SByte", "Single", "String", "Time", "UInt16", "UInt32", "UInt64", "VarNumeric", "AnsiStringFixedLength",
            "StringFixedLength", null!, "Xml", "DateTime2", "DateTimeOffset",
        ];

        public string? Name { get; private set; }

        public string? DbType { get; private set; }

        public int? Size { get; private set; }

        public int? Precision { get; private set; }

        public int? Scale { get; private set; }

        public static ParameterOverrides Read(IParameterSymbol parameter)
        {
            var result = new ParameterOverrides();
            var attribute = parameter.GetAttributes().FirstOrDefault(a => TypeFacts.IsType(a.AttributeClass, "VelQuery", "VelQueryParameterAttribute"));
            if (attribute is null)
            {
                return result;
            }

            foreach (var named in attribute.NamedArguments)
            {
                switch (named.Key)
                {
                    case "Name" when named.Value.Value is string { Length: > 0 } name:
                        result.Name = name.TrimStart('@');
                        break;
                    case "DbType" when named.Value.Value is int dbType && dbType >= 0 && dbType < DbTypeNames.Length && DbTypeNames[dbType] is not null:
                        result.DbType = DbTypeNames[dbType];
                        break;
                    case "Size" when named.Value.Value is int size:
                        result.Size = size;
                        break;
                    case "Precision" when named.Value.Value is byte precision:
                        result.Precision = precision;
                        break;
                    case "Scale" when named.Value.Value is byte scale:
                        result.Scale = scale;
                        break;
                }
            }

            return result;
        }
    }
}
