using System.Data;

namespace VelQuery.Parameters;

/// <summary>
/// Immutable description of a command parameter (VQ-FR-008). <see cref="Name"/> excludes any dialect prefix such as
/// <c>@</c>; providers apply their own naming.
/// </summary>
/// <param name="Name">Parameter name without prefix.</param>
/// <param name="DbType">Generic database type, or <see langword="null"/> to let VelQuery infer it from the CLR type.</param>
/// <param name="Size">Maximum size for strings and binary values; <c>-1</c> means MAX.</param>
/// <param name="Precision">Numeric precision.</param>
/// <param name="Scale">Numeric scale.</param>
/// <param name="Direction">Parameter direction.</param>
/// <param name="ProviderDbType">Provider-native type code (for example a <c>SqlDbType</c> value), interpreted by the provider.</param>
public readonly record struct ParameterSpec(
    string Name,
    DbType? DbType = null,
    int? Size = null,
    byte? Precision = null,
    byte? Scale = null,
    ParameterDirection Direction = ParameterDirection.Input,
    int? ProviderDbType = null);
