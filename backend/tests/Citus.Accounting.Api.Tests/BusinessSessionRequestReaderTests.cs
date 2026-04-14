using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessSessionRequestReaderTests
{
    [Fact]
    public void TryRead_ReturnsContext_WhenRequiredHeadersArePresent()
    {
        var headers = new HeaderDictionary
        {
            [BusinessSessionHeaders.UserId] = "7bd0e908-cfe7-4f7b-8a0d-f19292e4186d",
            [BusinessSessionHeaders.ActiveCompanyId] = "5e492df2-37ab-47df-a1bb-2d559c876cbc"
        };

        var reader = new BusinessSessionRequestReader();

        var success = reader.TryRead(headers, out var context, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(context);
        Assert.Equal(Guid.Parse("7bd0e908-cfe7-4f7b-8a0d-f19292e4186d"), context.UserId);
        Assert.Equal(Guid.Parse("5e492df2-37ab-47df-a1bb-2d559c876cbc"), context.ActiveCompanyId);
    }

    [Fact]
    public void TryRead_ReturnsError_WhenHeadersAreMissing()
    {
        var reader = new BusinessSessionRequestReader();

        var success = reader.TryRead(new HeaderDictionary(), out var context, out var error);

        Assert.False(success);
        Assert.Null(context);
        Assert.Equal($"Missing required business session header '{BusinessSessionHeaders.UserId}'.", error);
    }

    [Fact]
    public void TryRead_ReturnsError_WhenHeaderIsNotGuid()
    {
        var headers = new HeaderDictionary
        {
            [BusinessSessionHeaders.UserId] = "not-a-guid",
            [BusinessSessionHeaders.ActiveCompanyId] = "5e492df2-37ab-47df-a1bb-2d559c876cbc"
        };

        var reader = new BusinessSessionRequestReader();

        var success = reader.TryRead(headers, out _, out var error);

        Assert.False(success);
        Assert.Equal($"Header '{BusinessSessionHeaders.UserId}' must be a valid GUID.", error);
    }
}
