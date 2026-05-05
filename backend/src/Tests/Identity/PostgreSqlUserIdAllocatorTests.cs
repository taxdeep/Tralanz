using Infrastructure.PostgreSQL.Identity;
using Npgsql;
using SharedKernel.Identity;

namespace Tests.Identity;

public sealed class PostgreSqlUserIdAllocatorTests
{
    [Fact]
    public async Task Allocate_FirstCall_ReturnsOrdinalOne()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var allocator = new PostgreSqlUserIdAllocator();
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();

            var id = await allocator.AllocateAsync(connection, transaction: null, CancellationToken.None);

            Assert.Equal(1L, id.Ordinal);
            Assert.Equal("U000001", id.Value);
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }

    [Fact]
    public async Task Allocate_SequentialCalls_IncrementOrdinal()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var allocator = new PostgreSqlUserIdAllocator();
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();

            var first = await allocator.AllocateAsync(connection, null, CancellationToken.None);
            var second = await allocator.AllocateAsync(connection, null, CancellationToken.None);
            var third = await allocator.AllocateAsync(connection, null, CancellationToken.None);

            Assert.Equal(UserId.FromOrdinal(1), first);
            Assert.Equal(UserId.FromOrdinal(2), second);
            Assert.Equal(UserId.FromOrdinal(3), third);
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }

    [Fact]
    public async Task Allocate_TransactionRollback_NoGap()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var allocator = new PostgreSqlUserIdAllocator();
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();

            UserId aborted;
            await using (var tx = await connection.BeginTransactionAsync())
            {
                aborted = await allocator.AllocateAsync(connection, tx, CancellationToken.None);
                await tx.RollbackAsync();
            }
            Assert.Equal(UserId.FromOrdinal(1), aborted);

            var next = await allocator.AllocateAsync(connection, null, CancellationToken.None);
            Assert.Equal(UserId.FromOrdinal(1), next); // rolled back, so 1 reissued
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }
}
