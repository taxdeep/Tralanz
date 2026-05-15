using Infrastructure.PostgreSQL;
using Infrastructure.PostgreSQL.AP.Bills;

namespace Tests.AP;

public sealed class LegacyBillPostingGuardTests
{
    [Fact]
    public async Task PostAsync_BlocksLegacyStatusOnlyPosting()
    {
        var store = new PostgreSqlBillStore(new PostgreSqlConnectionFactory(
            "Host=localhost;Database=unused;Username=unused;Password=unused"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PostAsync(
                CompanyId.FromOrdinal(1),
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                CancellationToken.None));

        Assert.Contains("journal entry and AP open item", ex.Message, StringComparison.Ordinal);
    }
}
