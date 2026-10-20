using System.Collections.Generic;
using MongoDB.Driver;

namespace VelQuery.MongoDB;

/// <summary>
/// A write command (<c>insert</c>, <c>update</c>, <c>delete</c>) that the server answered with write errors or a write
/// concern error. Surfaces as the inner exception of <see cref="VelQueryProviderException"/> (or
/// <see cref="VelQueryTimeoutException"/> for time-limit codes). The message carries command names, counts, indexes and
/// codes only; the server's own text (which can contain key values) is on <see cref="MongoWriteCommandError.ServerMessage"/>.
/// </summary>
public sealed class MongoWriteCommandException : MongoException
{
    /// <summary>Creates the exception.</summary>
    public MongoWriteCommandException(string commandName, long count, IReadOnlyList<MongoWriteCommandError> writeErrors, MongoWriteCommandError? writeConcernError)
        : base(Describe(commandName, count, writeErrors, writeConcernError))
    {
        CommandName = commandName;
        Count = count;
        WriteErrors = writeErrors;
        WriteConcernError = writeConcernError;
    }

    /// <summary>The write command, for example <c>insert</c>.</summary>
    public string CommandName { get; }

    /// <summary>
    /// What the server reported as written before the failure: inserted documents for <c>insert</c> (across all
    /// batches of a bulk insert), matched documents for <c>update</c>, deleted documents for <c>delete</c>.
    /// </summary>
    public long Count { get; }

    /// <summary>Write errors; for ordered writes there is at most one, at the first failing document.</summary>
    public IReadOnlyList<MongoWriteCommandError> WriteErrors { get; }

    /// <summary>The write concern error, when the writes were applied but the requested acknowledgement failed.</summary>
    public MongoWriteCommandError? WriteConcernError { get; }

    /// <summary>The first error's server code (for example 11000, duplicate key).</summary>
    public int Code => WriteErrors.Count > 0 ? WriteErrors[0].Code : WriteConcernError?.Code ?? 0;

    /// <summary>True when the first write error is a duplicate key (11000 or 11001).</summary>
    public bool IsDuplicateKey => WriteErrors.Count > 0 && WriteErrors[0].Code is 11000 or 11001;

    private static string Describe(string commandName, long count, IReadOnlyList<MongoWriteCommandError> errors, MongoWriteCommandError? concern)
        => errors.Count > 0
            ? $"The '{commandName}' command failed at index {errors[0].Index} with server code {errors[0].Code} after {count} document(s) were written."
            : $"The '{commandName}' command wrote {count} document(s) but the write concern failed with server code {concern?.Code}.";
}

/// <summary>One server-reported write error.</summary>
/// <param name="Index">Position of the failing document or statement; for bulk inserts, across the whole sequence.</param>
/// <param name="Code">Server error code.</param>
/// <param name="CodeName">Server error code name, when reported.</param>
/// <param name="ServerMessage">The server's message. It can contain key values, so VelQuery never copies it into its own messages.</param>
public sealed record MongoWriteCommandError(long Index, int Code, string? CodeName, string ServerMessage);
