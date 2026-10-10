using System.Globalization;
using System.Runtime.CompilerServices;

namespace VelQuery.Execution;

/// <summary>
/// Stable identities for runtime SQL without an explicit query ID: <c>sql:</c> plus a 64-bit FNV-1a hash of the SQL
/// text. SQL text never contains bound values, so the identity is value-free (VQ-FR-025).
/// </summary>
internal static class QueryIdentity
{
    private static readonly ConditionalWeakTable<string, string> s_cache = [];

    public static string ForSql(string sql) => s_cache.GetValue(sql, static s => Compute(s));

    private static string Compute(string sql)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var c in sql)
        {
            hash = (hash ^ c) * prime;
        }

        return "sql:" + hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
