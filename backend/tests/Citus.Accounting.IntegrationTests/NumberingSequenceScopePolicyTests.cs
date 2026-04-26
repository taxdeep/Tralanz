using System.Reflection;
using Xunit;

namespace Citus.Accounting.IntegrationTests;

public sealed class NumberingSequenceScopePolicyTests
{
    private static readonly Type[] SequenceTypes =
    [
        Type.GetType("Citus.Accounting.Infrastructure.Persistence.PostgresNumberingSequences, Citus.Accounting.Infrastructure", throwOnError: true)!,
        Type.GetType("Citus.Accounting.Infrastructure.Persistence.PostgresSourceDocumentDraftNumbering, Citus.Accounting.Infrastructure", throwOnError: true)!,
        Type.GetType("Infrastructure.PostgreSQL.Numbering.PostgreSqlNumberingSequences, Infrastructure.PostgreSQL", throwOnError: true)!
    ];

    [Fact]
    public void EntityNumberScopesResolveToYearOnlyAcrossModules()
    {
        foreach (var sequenceType in SequenceTypes)
        {
            var invoiceYear = ParseEntityNumberYear(sequenceType, "entity-number:invoice:2026", "EN2026");
            var journalYear = ParseEntityNumberYear(sequenceType, "entity-number:journal-entry:2026", "EN2026");

            Assert.Equal(2026, invoiceYear);
            Assert.Equal(2026, journalYear);
        }
    }

    [Fact]
    public void DisplayNumberScopesAreNotParsedAsEntityNumberScopes()
    {
        foreach (var sequenceType in SequenceTypes)
        {
            var year = ParseEntityNumberYear(sequenceType, "invoice-display-number", "INV-");

            Assert.Null(year);
        }
    }

    private static int? ParseEntityNumberYear(Type sequenceType, string scopeKey, string prefix)
    {
        var method = sequenceType.GetMethod("TryParseEntityNumberYear", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [scopeKey, prefix]);
        return result is null ? null : Assert.IsType<int>(result);
    }
}
