using Npgsql;
using SharedKernel.Identity;

namespace Infrastructure.PostgreSQL.Identity;

public interface IUserIdAllocator
{
    Task<UserId> AllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken);
}
