using System.Data.Common;

namespace VelQuery;

/// <summary>
/// The connection lent to a provider-specific operation by <see cref="IVelQuerySession.ExecuteOnConnectionAsync"/>.
/// The operation must not dispose or close the connection, and must not commit or roll back the transaction.
/// </summary>
/// <param name="Connection">An open connection: the session's pinned connection, or a pooled one owned by VelQuery for this call.</param>
/// <param name="Transaction">The session's active transaction, which the operation must enlist in; otherwise <see langword="null"/>.</param>
/// <param name="CommandTimeoutSeconds">The resolved session/default timeout in ADO.NET seconds (0 = none), or <see langword="null"/> for the provider default.</param>
public readonly record struct ConnectionContext(DbConnection Connection, DbTransaction? Transaction, int? CommandTimeoutSeconds);
