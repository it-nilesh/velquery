using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace VelQuery.Generators.Infrastructure;

/// <summary>A value-equatable source location; pipeline models must not hold <see cref="Location"/> or symbols.</summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
    {
        if (location is null || location.SourceTree is null)
        {
            return null;
        }

        return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    public static LocationInfo? From(SyntaxNode? node) => From(node?.GetLocation());

    public static LocationInfo? From(SyntaxTree tree, TextSpan span)
        => new(tree.FilePath, span, tree.GetLineSpan(span).Span);
}

/// <summary>A value-equatable diagnostic, materialized only when reported.</summary>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments)
        => new(descriptor, location, new EquatableArray<string>(arguments));

    public Diagnostic ToDiagnostic()
        => Diagnostic.Create(Descriptor, Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None, Arguments.Cast<object>().ToArray());

    public bool Equals(DiagnosticInfo? other)
        => other is not null
            && ReferenceEquals(Descriptor, other.Descriptor)
            && Equals(Location, other.Location)
            && Arguments.Equals(other.Arguments);

    public override int GetHashCode() => HashCode(Descriptor.Id, Location, Arguments);

    private static int HashCode(string id, LocationInfo? location, EquatableArray<string> arguments)
    {
        unchecked
        {
            return (((StringComparer.Ordinal.GetHashCode(id) * 31) + (location?.GetHashCode() ?? 0)) * 31) + arguments.GetHashCode();
        }
    }
}
