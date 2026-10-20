using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using VelQuery.Generators.Infrastructure;
using VelQuery.MongoDB.Generators.Diagnostics;
using VelQuery.MongoDB.Generators.Model;

namespace VelQuery.MongoDB.Generators.Analysis;

/// <summary>
/// Builds compile-time serializer plans for a document type and every document type it embeds (ADR-0019):
/// <list type="bullet">
/// <item>Construction mirrors the SQL row mapper (ADR-0004): a public parameterless constructor plus settable
/// properties, otherwise the public constructor with the most parameters (for example a record's primary
/// constructor).</item>
/// <item>Field names: <c>[BsonElement("name")]</c>, else <c>[BsonId]</c> or a member named <c>Id</c>/<c>id</c>/<c>_id</c>
/// maps to <c>_id</c>, else the member name exactly (the driver's default convention). Matching is case-sensitive.</item>
/// <item>Constructor parameters and <c>required</c>/<c>init</c> properties are required: a missing field fails
/// unless the member is nullable (MongoDB treats a missing field like null). Other settable properties keep their
/// initializer value when the field is missing. BSON null into a non-nullable member always fails.</item>
/// <item><c>[BsonIgnore]</c> skips a member; <c>[BsonIgnoreIfNull]</c> omits nulls when writing. Other driver mapping
/// attributes would change the mapping silently, so they are rejected (VQ2003).</item>
/// </list>
/// </summary>
internal sealed class DocumentPlanBuilder
{
    private const string AttributesNamespace = "MongoDB.Bson.Serialization.Attributes";

    private static readonly HashSet<string> SupportedAttributes = new(StringComparer.Ordinal)
    {
        "BsonElementAttribute", "BsonIdAttribute", "BsonIgnoreAttribute", "BsonIgnoreIfNullAttribute", "BsonIgnoreExtraElementsAttribute",
    };

    private readonly Dictionary<string, DocumentPlan?> _plans = new(StringComparer.Ordinal);
    private readonly List<DiagnosticInfo> _diagnostics;
    private readonly LocationInfo? _location;

    public DocumentPlanBuilder(List<DiagnosticInfo> diagnostics, LocationInfo? location)
    {
        _diagnostics = diagnostics;
        _location = location;
    }

    /// <summary>All successfully planned document types, ordered by type name.</summary>
    public EquatableArray<DocumentPlan> Plans
        => _plans.Values.Where(p => p is not null).OrderBy(p => p!.Type, StringComparer.Ordinal).Select(p => p!).ToEquatableArray();

    /// <summary>Plans <paramref name="type"/> (and embedded types); returns its serializer class name or null on failure.</summary>
    public string? Build(ITypeSymbol type)
    {
        var display = MongoTypeFacts.DisplayNoAnnotations(type);
        var serializer = "BsonSerializer_" + MongoTypeFacts.Sanitize(display);
        if (_plans.TryGetValue(display, out var existing))
        {
            // Null while in progress (recursive types reference their own serializer) or after a reported failure.
            return existing is null && !_inProgress.Contains(display) ? null : serializer;
        }

        _inProgress.Add(display);
        _plans[display] = null;
        var plan = BuildCore(type, display, serializer);
        _inProgress.Remove(display);
        _plans[display] = plan;
        return plan is null ? null : serializer;
    }

    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);

    private DocumentPlan? BuildCore(ITypeSymbol rowType, string display, string serializer)
    {
        DocumentPlan? Reject(string reason)
        {
            _diagnostics.Add(DiagnosticInfo.Create(Descriptors.UnsupportedDocumentType, _location, display, reason));
            return null;
        }

        if (rowType.TypeKind == TypeKind.Error)
        {
            return null; // The compiler reports the unresolved type.
        }

        if (rowType is not INamedTypeSymbol type || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return Reject("only classes, records and structs can be mapped from BSON documents");
        }

        if (MongoTypeFacts.NullableUnderlying(type) is not null)
        {
            return Reject("nullable struct document types are not supported; use a class or record");
        }

        if (MongoTypeFacts.Scalar(type) is not null || MongoTypeFacts.SequenceElement(type) is not null)
        {
            return Reject("scalar and collection types are values, not documents; wrap them in a document type (for example a record with one member)");
        }

        if (type.IsAbstract || type.IsStatic)
        {
            return Reject("the type is abstract or static (polymorphic documents and discriminators are not supported)");
        }

        if (type.IsGenericType)
        {
            return Reject("generic document types are not supported");
        }

        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Reject("the type must be public or internal so generated code can construct it");
            }
        }

        var unsupported = UnsupportedAttribute(type);
        if (unsupported is not null)
        {
            return Reject($"attribute [{unsupported}] is not supported by VelQuery's generated serializer (it would change the mapping silently)");
        }

        var properties = CollectProperties(type);
        var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        var constructor = constructors.FirstOrDefault(c => c.Parameters.Length == 0);
        if (constructor is null)
        {
            var max = constructors.Count == 0 ? -1 : constructors.Max(c => c.Parameters.Length);
            var candidates = constructors.Where(c => c.Parameters.Length == max).ToList();
            if (candidates.Count != 1)
            {
                return Reject(candidates.Count == 0
                    ? "it has no public constructor"
                    : $"it has {candidates.Count} public constructors with {max} parameters; the constructor to use is ambiguous");
            }

            constructor = candidates[0];
        }

        var fields = new List<FieldPlan>();
        var origins = new List<string>();
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasExplicitId = properties.Any(p => HasAttribute(p, "BsonIdAttribute")) || constructor.Parameters.Any(p => HasAttribute(p, "BsonIdAttribute"));
        for (var index = 0; index < constructor.Parameters.Length; index++)
        {
            var parameter = constructor.Parameters[index];
            if (parameter.RefKind is not (RefKind.None or RefKind.In) || parameter.IsParams)
            {
                return Reject($"constructor parameter '{parameter.Name}' is ref, out or params");
            }

            var property = properties.FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (property is null || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
            {
                return Reject($"constructor parameter '{parameter.Name}' has no public readable property of the same name, so documents could not be written back");
            }

            var attributeIssue = UnsupportedAttribute(property) ?? UnsupportedAttribute(parameter);
            if (attributeIssue is not null)
            {
                return Reject($"attribute [{attributeIssue}] on '{parameter.Name}' is not supported by VelQuery's generated serializer (it would change the mapping silently)");
            }

            var plan = CreateField(index, MongoTypeFacts.Identifier(parameter.Name), MongoTypeFacts.Identifier(property.Name), FieldName(property, parameter, hasExplicitId),
                parameter.Type, FieldRole.Constructor, HasAttribute(property, "BsonIgnoreIfNullAttribute"), out var reason);
            if (plan is null)
            {
                return Reject($"constructor parameter '{parameter.Name}' {reason}");
            }

            fields.Add(plan);
            origins.Add(parameter.Name);
            bound.Add(parameter.Name);
        }

        foreach (var property in properties)
        {
            if (bound.Contains(property.Name) || HasAttribute(property, "BsonIgnoreAttribute"))
            {
                continue;
            }

            if (property.SetMethod is not { DeclaredAccessibility: Accessibility.Public } setter || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
            {
                continue; // Computed or read-only members are not stored.
            }

            var attributeIssue = UnsupportedAttribute(property);
            if (attributeIssue is not null)
            {
                return Reject($"attribute [{attributeIssue}] on '{property.Name}' is not supported by VelQuery's generated serializer (it would change the mapping silently)");
            }

            var role = property.IsRequired || setter.IsInitOnly ? FieldRole.Initializer : FieldRole.Optional;
            var identifier = MongoTypeFacts.Identifier(property.Name);
            var plan = CreateField(-1, identifier, identifier, FieldName(property, null, hasExplicitId), property.Type, role,
                HasAttribute(property, "BsonIgnoreIfNullAttribute"), out var reason);
            if (plan is null)
            {
                return Reject($"property '{property.Name}' {reason} (mark it [BsonIgnore] to skip it)");
            }

            fields.Add(plan);
            origins.Add(property.Name);
        }

        if (fields.Count == 0)
        {
            return Reject("it has no constructor parameters or settable public properties to map");
        }

        var ambiguous = false;
        for (var i = 0; i < fields.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(fields[i].Field, fields[j].Field, StringComparison.Ordinal))
                {
                    _diagnostics.Add(DiagnosticInfo.Create(Descriptors.AmbiguousField, _location, display, origins[j], origins[i], fields[i].Field));
                    ambiguous = true;
                }
            }
        }

        if (ambiguous)
        {
            return null;
        }

        // _id first: MongoDB stores it first anyway, and readers usually want it early.
        var ordered = fields.Where(f => f.Field == "_id").Concat(fields.Where(f => f.Field != "_id")).ToEquatableArray();
        return new DocumentPlan(display, serializer, "VelQuery.Generated.Mongo." + serializer + ".g.cs", ordered);
    }

    private FieldPlan? CreateField(int constructorIndex, string member, string property, string field, ITypeSymbol type, FieldRole role, bool ignoreIfNull, out string reason)
    {
        var shape = MemberShape(type, out reason);
        if (shape is null)
        {
            return null;
        }

        var omitEmptyId = field == "_id" && shape is { Kind: ShapeKind.Scalar, Scalar: "ObjectId", Nullable: false };
        return new FieldPlan(constructorIndex, member, property, field, shape, role, ignoreIfNull && shape.Nullable, omitEmptyId);
    }

    private ValueShape? MemberShape(ITypeSymbol type, out string reason)
    {
        reason = string.Empty;
        var scalar = MongoTypeFacts.ScalarShape(type, allowDocument: true);
        if (scalar is not null)
        {
            return scalar;
        }

        var valueType = MongoTypeFacts.NullableUnderlying(type) ?? type;
        if (valueType is INamedTypeSymbol { TypeKind: TypeKind.Enum })
        {
            reason = "is an enum whose underlying type is not int or long";
            return null;
        }

        var element = MongoTypeFacts.SequenceElement(type);
        if (element is not null)
        {
            if (!IsSupportedCollection(type))
            {
                reason = $"has collection type '{MongoTypeFacts.Display(type)}'; use T[], List<T>, IList<T>, ICollection<T>, IReadOnlyList<T>, IReadOnlyCollection<T> or IEnumerable<T>";
                return null;
            }

            var elementShape = MemberShape(element, out var elementReason);
            if (elementShape is null)
            {
                reason = "has an element type that " + elementReason;
                return null;
            }

            return new ValueShape(ShapeKind.List, string.Empty, MongoTypeFacts.Display(type), MongoTypeFacts.BareTypeOf(type), MongoTypeFacts.AcceptsNull(type),
                null, null, null, elementShape, type is IArrayTypeSymbol);
        }

        if (MongoTypeFacts.IsBsonValue(type) || valueType.TypeKind is TypeKind.Interface or TypeKind.TypeParameter or TypeKind.Pointer or TypeKind.FunctionPointer)
        {
            reason = $"has unsupported type '{MongoTypeFacts.Display(type)}' (supported: {MongoTypeFacts.SupportedScalars}, BsonDocument, collections and nested document types)";
            return null;
        }

        if (valueType is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } nested && IsCandidateDocument(nested))
        {
            if (type.IsValueType && MongoTypeFacts.NullableUnderlying(type) is not null)
            {
                reason = "is a nullable struct; embedded documents that can be null must be classes or records";
                return null;
            }

            var nestedSerializer = Build(nested);
            if (nestedSerializer is null)
            {
                reason = $"has embedded document type '{MongoTypeFacts.DisplayNoAnnotations(nested)}', which cannot be mapped (see the other VQ2003 diagnostic)";
                return null;
            }

            return new ValueShape(ShapeKind.Document, string.Empty, MongoTypeFacts.Display(type), MongoTypeFacts.BareTypeOf(type), MongoTypeFacts.AcceptsNull(type),
                null, null, nestedSerializer, null, false);
        }

        reason = $"has unsupported type '{MongoTypeFacts.Display(type)}' (supported: {MongoTypeFacts.SupportedScalars}, BsonDocument, collections and nested document types)";
        return null;
    }

    /// <summary>Framework types (DateTimeOffset, TimeSpan, dictionaries, …) are not treated as embedded documents.</summary>
    private static bool IsCandidateDocument(INamedTypeSymbol type)
    {
        for (var ns = type.ContainingNamespace; ns is not null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
        {
            if (ns.ContainingNamespace is { IsGlobalNamespace: true } && ns.Name is "System" or "Microsoft" or "MongoDB")
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { Rank: 1 })
        {
            return true;
        }

        var valueType = MongoTypeFacts.NullableUnderlying(type) ?? type;
        return valueType is INamedTypeSymbol { Arity: 1 } named
            && (MongoTypeFacts.IsType(named, "System.Collections.Generic", "List", 1)
                || MongoTypeFacts.IsType(named, "System.Collections.Generic", "IList", 1)
                || MongoTypeFacts.IsType(named, "System.Collections.Generic", "ICollection", 1)
                || MongoTypeFacts.IsType(named, "System.Collections.Generic", "IReadOnlyList", 1)
                || MongoTypeFacts.IsType(named, "System.Collections.Generic", "IReadOnlyCollection", 1)
                || MongoTypeFacts.IsType(named, "System.Collections.Generic", "IEnumerable", 1));
    }

    private static string FieldName(IPropertySymbol property, IParameterSymbol? parameter, bool hasExplicitId)
    {
        if (HasAttribute(property, "BsonIdAttribute") || (parameter is not null && HasAttribute(parameter, "BsonIdAttribute")))
        {
            return "_id";
        }

        var element = ElementName(property) ?? (parameter is null ? null : ElementName(parameter));
        if (element is not null)
        {
            return element;
        }

        // The driver's default convention: Id, id or _id is the document id unless another member is [BsonId].
        return !hasExplicitId && property.Name is "Id" or "id" or "_id" ? "_id" : property.Name;
    }

    private static string? ElementName(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (MongoTypeFacts.IsType(attribute.AttributeClass, AttributesNamespace, "BsonElementAttribute")
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    private static string? UnsupportedAttribute(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is not null
                && attributeClass.ContainingNamespace?.ToDisplayString() == AttributesNamespace
                && !SupportedAttributes.Contains(attributeClass.Name))
            {
                return attributeClass.Name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? attributeClass.Name.Substring(0, attributeClass.Name.Length - "Attribute".Length)
                    : attributeClass.Name;
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string name)
        => symbol.GetAttributes().Any(a => MongoTypeFacts.IsType(a.AttributeClass, AttributesNamespace, name));

    /// <summary>Public instance non-indexer properties, most-derived declaration first, in declaration order.</summary>
    private static List<IPropertySymbol> CollectProperties(INamedTypeSymbol type)
    {
        var result = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object && current.SpecialType != SpecialType.System_ValueType; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                    || property.Name == "EqualityContract" || !seen.Add(property.Name))
                {
                    continue;
                }

                result.Add(property);
            }
        }

        return result;
    }
}
