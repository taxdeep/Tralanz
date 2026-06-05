using Infrastructure.PostgreSQL.Identity;
using Npgsql;
using SharedKernel.Identity;

namespace Tests.Identity;

public sealed class PostgreSqlCompanyIdAllocatorTests
{
    [SkippableFact]
    public async Task Allocate_FirstCall_ReturnsOrdinalOne()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var allocator = new PostgreSqlCompanyIdAllocator();
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();
            await IdentityTestSchema.EnsurePlatformCompanyIdSequenceAsync(connection);

            var id = await allocator.AllocateAsync(connection, null, CancellationToken.None);

            Assert.Equal(1L, id.Ordinal);
            Assert.Equal("C000001", id.Value);
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }

    [SkippableFact]
    public async Task Allocate_IndependentFromUserCounter()
    {
        var baseConn = IdentityTestSchema.GetConnectionString();
        var schema = IdentityTestSchema.NewSchemaName();
        var schemaConn = IdentityTestSchema.BuildSchemaConnectionString(baseConn, schema);

        await IdentityTestSchema.CreateSchemaAsync(baseConn, schema);
        try
        {
            var userAllocator = new PostgreSqlUserIdAllocator();
            var companyAllocator = new PostgreSqlCompanyIdAllocator();
            await using var connection = new NpgsqlConnection(schemaConn);
            await connection.OpenAsync();
            await IdentityTestSchema.EnsurePlatformUserIdSequenceAsync(connection);
            await IdentityTestSchema.EnsurePlatformCompanyIdSequenceAsync(connection);

            await userAllocator.AllocateAsync(connection, null, CancellationToken.None); // U000001
            await userAllocator.AllocateAsync(connection, null, CancellationToken.None); // U000002

            var company = await companyAllocator.AllocateAsync(connection, null, CancellationToken.None);
            Assert.Equal(CompanyId.FromOrdinal(1), company); // company counter starts fresh
        }
        finally
        {
            await IdentityTestSchema.DropSchemaAsync(baseConn, schema);
        }
    }
}
