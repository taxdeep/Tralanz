using Modules.CompanyAccess.SystemSetup;
using SharedKernel.CompanyAccess;

namespace Tests.CompanyAccess;

public sealed class SystemSetupWorkflowTests
{
    [Fact]
    public async Task SaveNumberDisplayModeAsync_ParsesModeCodeAndPersists()
    {
        var store = new StubStore();
        var workflow = new SystemSetupWorkflow(store);
        var userId = Guid.NewGuid();

        var saved = await workflow.SaveNumberDisplayModeAsync(userId, "dot-comma", CancellationToken.None);

        Assert.Equal(NumberDisplayMode.DotComma, saved.NumberDisplayMode);
        Assert.Equal(NumberDisplayMode.DotComma, store.Preference.NumberDisplayMode);
    }

    [Fact]
    public void NumberDisplayFormatting_FormatsAllSupportedModes()
    {
        Assert.Equal("1,234.56", NumberDisplayFormatting.FormatAmount(1234.56m, NumberDisplayMode.CommaDot));
        Assert.Equal("1.234,56", NumberDisplayFormatting.FormatAmount(1234.56m, NumberDisplayMode.DotComma));
        Assert.Equal("1 234,56", NumberDisplayFormatting.FormatAmount(1234.56m, NumberDisplayMode.SpaceComma));
        Assert.Equal("1'234.56", NumberDisplayFormatting.FormatAmount(1234.56m, NumberDisplayMode.ApostropheDot));
    }

    private sealed class StubStore : ISystemSetupStore
    {
        public SystemSetupPreference Preference { get; private set; } =
            new(Guid.Empty, NumberDisplayModeDefaults.Default, DateTimeOffset.UtcNow);

        public Task<SystemSetupPreference> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Preference with { UserId = userId });

        public Task<SystemSetupPreference> SaveAsync(
            Guid userId,
            NumberDisplayMode numberDisplayMode,
            CancellationToken cancellationToken)
        {
            Preference = new SystemSetupPreference(userId, numberDisplayMode, DateTimeOffset.UtcNow);
            return Task.FromResult(Preference);
        }
    }
}
