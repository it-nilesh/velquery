using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VelQuery.Core.UnitTests.Fakes;
using Xunit;

namespace VelQuery.Core.UnitTests;

/// <summary>Every public execution shape, so lifecycle tests can run as theories across all of them.</summary>
public enum Op
{
    Query,
    First,
    FirstOrDefault,
    QuerySingle,
    QuerySingleOrDefault,
    Stream,
    Execute,
    Scalar,
    Multi,
    DescriptorQuery,
    DescriptorSingle,
    DescriptorStream,
    DescriptorExecute,
    DescriptorScalar,
    DescriptorMulti,
}

internal static class Operations
{
    public static readonly Op[] All = Enum.GetValues<Op>();

    /// <summary>Operations that read every row of every result set (so read-stage faults are always reached).</summary>
    public static readonly Op[] FullRead = [Op.Query, Op.Stream, Op.Multi, Op.DescriptorQuery, Op.DescriptorStream, Op.DescriptorMulti];

    public static TheoryData<Op> AllData() => new(All);

    public static TheoryData<Op> FullReadData() => new(FullRead);

    public static TheoryData<Op> ReaderData() => new(All.Where(UsesReader));

    public static bool UsesReader(Op op) => op is not (Op.Execute or Op.Scalar or Op.DescriptorExecute or Op.DescriptorScalar);

    public static async Task RunAsync(VelQuerySession session, Op op, string sql, CancellationToken cancellationToken)
    {
        var descriptor = Descriptors.Scalar<int>(sql, "ops.query");
        switch (op)
        {
            case Op.Query:
                await session.QueryAsync<int>(sql, null, cancellationToken);
                break;
            case Op.First:
                await session.QueryFirstAsync<int>(sql, null, cancellationToken);
                break;
            case Op.FirstOrDefault:
                await session.QueryFirstOrDefaultAsync<int>(sql, null, cancellationToken);
                break;
            case Op.QuerySingle:
                await session.QuerySingleAsync<int>(sql, null, cancellationToken);
                break;
            case Op.QuerySingleOrDefault:
                await session.QuerySingleOrDefaultAsync<int>(sql, null, cancellationToken);
                break;
            case Op.Stream:
                await foreach (var _ in session.StreamAsync<int>(sql, null, cancellationToken))
                {
                }

                break;
            case Op.Execute:
                await session.ExecuteAsync(sql, null, cancellationToken);
                break;
            case Op.Scalar:
                await session.ExecuteScalarAsync<int>(sql, null, cancellationToken);
                break;
            case Op.Multi:
                await using (var multi = await session.QueryMultipleAsync(sql, null, cancellationToken))
                {
                    while (multi.HasMoreResults)
                    {
                        await multi.ReadAsync<int>(cancellationToken);
                    }
                }

                break;
            case Op.DescriptorQuery:
                await session.QueryAsync(descriptor, new NoArgs(), cancellationToken);
                break;
            case Op.DescriptorSingle:
                await session.QuerySingleAsync(descriptor, new NoArgs(), cancellationToken);
                break;
            case Op.DescriptorStream:
                await foreach (var _ in session.StreamAsync(descriptor, new NoArgs(), cancellationToken))
                {
                }

                break;
            case Op.DescriptorExecute:
                await session.ExecuteAsync(new QueryDescriptor<NoArgs>("ops.execute", sql), new NoArgs(), cancellationToken);
                break;
            case Op.DescriptorScalar:
                await session.ExecuteScalarAsync(descriptor, new NoArgs(), cancellationToken);
                break;
            case Op.DescriptorMulti:
                await using (var multi = await session.QueryMultipleAsync(new QueryDescriptor<NoArgs>("ops.multi", sql), new NoArgs(), cancellationToken))
                {
                    while (multi.HasMoreResults)
                    {
                        await multi.ReadAsync(Mapping.ScalarRowMapperFactory<int>.Instance, cancellationToken);
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    /// <summary>Asserts the command was cancelled exactly once and before its reader was disposed (no server drain).</summary>
    public static void AssertCancelledBeforeReaderDispose(FakeDatabase db)
    {
        var command = Assert.Single(db.Commands);
        var reader = Assert.Single(db.Readers);
        Assert.Equal(1, command.CancelCount);
        var cancel = db.IndexOf($"{command}:Cancel");
        var dispose = db.IndexOf($"{reader}:Dispose");
        Assert.True(cancel >= 0 && cancel < dispose, $"Cancel ({cancel}) must precede reader dispose ({dispose}).");
    }

    public static void AssertNotCancelled(FakeDatabase db)
        => Assert.All(db.Commands, c => Assert.Equal(0, c.CancelCount));
}
