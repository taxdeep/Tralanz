using Infrastructure.PostgreSQL.Identity;
using Npgsql;
using SharedKernel.Identity;

namespace Tests.Identity;

public sealed class PostgreSqlEntityNumberAllocatorTests
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
            var allocator = new PostgreSqlEntityNumberAllocator();
            var company = CompanyId.FromOrdinal(1);
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();
            await IdentityTestSchema.EnsureEntityNumberSequenceTableAsync(connection);
            await using var tx = await connection.BeginTransactionAsync();

            var en = await allocator.AllocateAsync(connection, tx, company, 2026, CancellationToken.None);
            await tx.CommitAsync();

            Assert.Equal(1L, en.Ordinal);
            Assert.Equal(2026, en.Year);
            Assert.Equal("EN202600001", en.Value);
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }

    [Fact]
    public async Task Allocate_DifferentCompanies_HaveIndependentCounters()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var allocator = new PostgreSqlEntityNumberAllocator();
            var companyA = CompanyId.FromOrdinal(1);
            var companyB = CompanyId.FromOrdinal(2);
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();
            await IdentityTestSchema.EnsureEntityNumberSequenceTableAsync(connection);

            EntityNumber a1, a2, b1;
            await using (var tx = await connection.BeginTransactionAsync())
            {
                a1 = await allocator.AllocateAsync(connection, tx, companyA, 2026, CancellationToken.None);
                a2 = await allocator.AllocateAsync(connection, tx, companyA, 2026, CancellationToken.None);
                b1 = await allocator.AllocateAsync(connection, tx, companyB, 2026, CancellationToken.None);
                await tx.CommitAsync();
            }

            Assert.Equal(1L, a1.Ordinal);
            Assert.Equal(2L, a2.Ordinal);
            Assert.Equal(1L, b1.Ordinal); // independent counter for company B
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }

    [Fact]
    public async Task Allocate_DifferentYears_HaveIndependentCounters()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var allocator = new PostgreSqlEntityNumberAllocator();
            var company = CompanyId.FromOrdinal(1);
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();
            await IdentityTestSchema.EnsureEntityNumberSequenceTableAsync(connection);

            EntityNumber y2026, y2027;
            await using (var tx = await connection.BeginTransactionAsync())
            {
                y2026 = await allocator.AllocateAsync(connection, tx, company, 2026, CancellationToken.None);
                y2027 = await allocator.AllocateAsync(connection, tx, company, 2027, CancellationToken.None);
                await tx.CommitAsync();
            }

            Assert.Equal(1L, y2026.Ordinal);
            Assert.Equal(2026, y2026.Year);
            Assert.Equal(1L, y2027.Ordinal); // independent counter for 2027
            Assert.Equal(2027, y2027.Year);
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
            var allocator = new PostgreSqlEntityNumberAllocator();
            var company = CompanyId.FromOrdinal(1);
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();
            await IdentityTestSchema.EnsureEntityNumberSequenceTableAsync(connection);

            EntityNumber aborted;
            await using (var tx1 = await connection.BeginTransactionAsync())
            {
                aborted = await allocator.AllocateAsync(connection, tx1, company, 2026, CancellationToken.None);
                await tx1.RollbackAsync();
            }
            Assert.Equal(1L, aborted.Ordinal);

            EntityNumber retry;
            await using (var tx2 = await connection.BeginTransactionAsync())
            {
                retry = await allocator.AllocateAsync(connection, tx2, company, 2026, CancellationToken.None);
                await tx2.CommitAsync();
            }
            Assert.Equal(1L, retry.Ordinal); // rolled back, so 1 reissued — no gap
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }

    [Fact]
    public async Task Allocate_RequiresTransaction()
    {
        var allocator = new PostgreSqlEntityNumberAllocator();
        var company = CompanyId.FromOrdinal(1);
        await using var connection = new NpgsqlConnection(IdentityTestSchema.GetConnectionString());
        // intentionally not opening — should fail before any DB access
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            allocator.AllocateAsync(connection, transaction: null!, company, 2026, CancellationToken.None));
    }
}
