using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Identity;

public interface IEntityNumberAllocator
{
    Task<EntityNumber> AllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CompanyId companyId,
        int year,
        CancellationToken cancellationToken);
}
