using Modules.GL.JournalEntry;
using SharedKernel.Company;

namespace Tests.GL;

public sealed class JournalEntryEditorStateTests
{
    [Fact]
    public void CreateDarkModeDemo_CreatesExpectedWorkbenchDefaults()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();

        Assert.True(state.Draft.IsDarkMode);
        Assert.Equal(CompanyId.FromOrdinal(1), state.Draft.CompanyId);
        Assert.Equal(string.Empty, state.Draft.JournalNumber);
        Assert.Equal("JE# Draft", state.Draft.Title);
        Assert.Equal("USD", state.Draft.CurrencyCode);
        Assert.Equal("CAD", state.Draft.BaseCurrencyCode);
        Assert.Equal("system_stored", state.Draft.FxSourceSemantics);
        Assert.Empty(state.AccountOptions);
        Assert.Equal(8, state.Draft.Lines.Count);
        Assert.All(state.Draft.Lines, line => Assert.False(line.HasContent));
    }

    [Fact]
    public void DuplicateInsertDeleteAndClear_KeepLineNumbersStable()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();
        state.Draft.Lines[0].Description = "Office subscription";

        state.DuplicateLine(1);
        state.InsertBlankAfter(2);
        state.DeleteLine(2);

        Assert.Equal(9, state.Draft.Lines.Count);
        Assert.Equal(Enumerable.Range(1, 9), state.Draft.Lines.Select(line => line.LineNumber));

        state.ClearAllLines();

        Assert.Equal(8, state.Draft.Lines.Count);
        Assert.All(state.Draft.Lines, line => Assert.False(line.HasContent));
    }

    [Fact]
    public void ApplyResolvedFxRate_CarriesSnapshotIdentity()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();
        var snapshotId = Guid.Parse("76000000-0000-0000-0000-000000000001");

        state.ApplyResolvedFxRate(new SharedKernel.FX.FxRateResolution(
            1.3845m,
            new DateOnly(2026, 4, 13),
            new DateOnly(2026, 4, 13),
            SharedKernel.FX.FxSourceSemantics.SystemStored,
            "Local snapshot",
            SharedKernel.FX.FxRateType.Spot,
            SharedKernel.FX.FxQuoteBasis.Direct,
            SharedKernel.FX.FxRateUseCase.General,
            SharedKernel.FX.FxPostingReason.Normal,
            "ECB",
            snapshotId));

        Assert.Equal(snapshotId, state.Draft.FxSnapshotId);
        Assert.Contains("Snapshot", state.GetFxSnapshotLabel());
    }

    [Fact]
    public void ManualFxRate_ClearsPersistedSnapshotAndConvertsLinePreview()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();
        state.Draft.FxSnapshotId = Guid.Parse("76000000-0000-0000-0000-000000000001");

        state.ApplyManualFxRate(1.37875m);

        Assert.Null(state.Draft.FxSnapshotId);
        Assert.Equal(137.88m, state.ConvertToBase(100m));
        Assert.True(state.ShouldShowBasePreview(100m));
    }

    [Fact]
    public void PostedStatus_MakesJournalReadOnlyAndUpdatesReviewTitle()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();
        state.Draft.Status = "posted";

        Assert.True(state.IsPosted);
        Assert.False(state.CanEditJournal);
        Assert.Equal("Posted FX review", state.GetFxReviewTitle());
    }

    [Fact]
    public void CurrentSnapshotAndMarketRate_AreResolvedFromSelection()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();
        var marketRateId = Guid.Parse("77000000-0000-0000-0000-000000000001");
        var snapshotId = Guid.Parse("76000000-0000-0000-0000-000000000001");
        state.Draft.FxSnapshotId = snapshotId;

        var snapshots = new[]
        {
            new SharedKernel.FX.FxSnapshotRecord(
                snapshotId,
                state.Draft.CompanyId,
                state.Draft.CurrencyCode,
                state.Draft.BaseCurrencyCode,
                state.Draft.JournalDate,
                state.Draft.JournalDate,
                state.Draft.FxRate,
                SharedKernel.FX.FxRateType.Spot,
                SharedKernel.FX.FxQuoteBasis.Direct,
                SharedKernel.FX.FxRateUseCase.General,
                SharedKernel.FX.FxPostingReason.Normal,
                "ECB",
                "provider_fetched",
                SharedKernel.FX.FxSourceSemantics.SystemStored,
                marketRateId,
                DateTimeOffset.UtcNow)
        };

        Assert.True(state.IsCurrentSnapshot(snapshotId));
        Assert.True(state.IsCurrentMarketRate(marketRateId, snapshots));
    }

    [Fact]
    public void ApplyCompanyCurrencyProfile_ReplacesEnabledCurrenciesAndBaseContext()
    {
        var state = JournalEntryEditorState.CreateDarkModeDemo();

        state.ApplyCompanyCurrencyProfile(new CompanyCurrencyProfile(
            Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc"),
            "Northwind Studio Ltd.",
            "USD",
            true,
            [
                new CompanyCurrencyOption("USD", "US Dollar", true, true),
                new CompanyCurrencyOption("CAD", "Canadian Dollar", false, true),
                new CompanyCurrencyOption("EUR", "Euro", false, false)
            ]));

        Assert.Equal("USD", state.Draft.BaseCurrencyCode);
        Assert.Equal("USD", state.Draft.CurrencyCode);
        Assert.Equal(2, state.CurrencyOptions.Count);
        Assert.DoesNotContain(state.CurrencyOptions, static option => option.Code == "EUR");
    }
}
