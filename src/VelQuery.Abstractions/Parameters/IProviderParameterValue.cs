namespace VelQuery.Parameters;

/// <summary>
/// A parameter value that its provider binds natively, for example a SQL Server table-valued parameter
/// (<c>VelQuery.SqlServer.SqlTableValue</c>). VelQuery binds it with <see cref="System.Data.DbType.Object"/> and passes it
/// to <see cref="Providers.IVelQueryProvider.ConfigureParameter"/>, which sets the native type and value. Generated
/// contracts accept such parameters without an explicit <c>DbType</c>.
/// </summary>
public interface IProviderParameterValue
{
}
