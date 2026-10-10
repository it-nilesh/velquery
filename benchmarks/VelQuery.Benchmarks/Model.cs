using System;

namespace VelQuery.Benchmarks;

/// <summary>Six-column projection used by every row-returning case. Mutable class so every library can map it.</summary>
public sealed class BenchRow
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public decimal Amount { get; set; }

    public DateTime CreatedUtc { get; set; }

    public bool IsActive { get; set; }
}

/// <summary>Values for the typed-params case, read back from the seeded row in global setup.</summary>
public sealed record TypedParamValues(int Id, string Name, decimal Amount, DateTime CreatedUtc, bool IsActive);

/// <summary>
/// The SQL text shared by all libraries. Every library executes exactly these strings (fairness control
/// <c>query-text-identical</c>).
/// </summary>
public static class BenchSql
{
    public const int OneRowId = 42;

    public const string OneRow =
        "SELECT Id, Name, Email, Amount, CreatedUtc, IsActive FROM dbo.BenchRows WHERE Id = @Id";

    public const string ScalarCount =
        "SELECT COUNT(*) FROM dbo.BenchRows WHERE Id <= 1000";

    public const string Top100 =
        "SELECT TOP (100) Id, Name, Email, Amount, CreatedUtc, IsActive FROM dbo.BenchRows ORDER BY Id";

    public const string Top1000 =
        "SELECT TOP (1000) Id, Name, Email, Amount, CreatedUtc, IsActive FROM dbo.BenchRows ORDER BY Id";

    public const string All =
        "SELECT Id, Name, Email, Amount, CreatedUtc, IsActive FROM dbo.BenchRows ORDER BY Id";

    public const string TypedParams =
        "SELECT Id, Name, Email, Amount, CreatedUtc, IsActive FROM dbo.BenchRows " +
        "WHERE Id = @Id AND Name = @Name AND Amount = @Amount AND CreatedUtc = @CreatedUtc AND IsActive = @IsActive";

    public static string Buffered(int rows) => rows switch
    {
        100 => Top100,
        1000 => Top1000,
        _ => throw new ArgumentOutOfRangeException(nameof(rows)),
    };

    // Declared parameter metadata for typed-params (matches the column definitions).
    public const int NameSize = 100;
    public const byte AmountPrecision = 18;
    public const byte AmountScale = 4;
}
