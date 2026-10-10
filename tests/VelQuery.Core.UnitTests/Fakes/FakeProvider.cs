using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Parameters;
using VelQuery.Providers;

namespace VelQuery.Core.UnitTests.Fakes;

/// <summary>Fake dialect: <c>@</c> parameter prefix and classification of <see cref="FakeDbException"/> flags.</summary>
internal sealed class FakeProvider : IVelQueryProvider
{
    private int _configureCalls;

    public string DialectId => "fake";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleResults | ProviderCapabilities.StoredProcedures | ProviderCapabilities.OutputParameters;

    public int ConfigureCalls => Volatile.Read(ref _configureCalls);

    public string FormatParameterName(string name) => "@" + name;

    public void ConfigureParameter(DbParameter parameter, in ParameterSpec spec, object? value)
        => Interlocked.Increment(ref _configureCalls);

    public string QuoteIdentifier(ReadOnlySpan<char> validatedIdentifier) => "[" + validatedIdentifier.ToString() + "]";

    public ProviderErrorKind Classify(Exception exception) => exception switch
    {
        FakeDbException { IsTimeout: true } => ProviderErrorKind.Timeout,
        FakeDbException { IsCancellation: true } => ProviderErrorKind.Cancelled,
        _ => ProviderErrorKind.Other,
    };
}

/// <summary>
/// Factory for VelQuery-owned connections. Like <see cref="DbDataSource.OpenConnectionAsync"/>, a connection whose
/// open fails is disposed by the factory before the exception propagates.
/// </summary>
internal sealed class FakeConnectionFactory(FakeDatabase db) : IVelQueryConnectionFactory
{
    public async ValueTask<DbConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = db.CreateFactoryConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
