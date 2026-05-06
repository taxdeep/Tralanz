using Microsoft.AspNetCore.Http;

namespace Citus.Accounting.Api.Tests;

public sealed class BusinessSessionRequestReaderTests
{
    [Fact]
    public void TryRead_ReturnsContext_WhenRequiredHeadersArePresent()
    {
        var expectedUserId = UserId.FromOrdinal(1);
        var expectedCompanyId = CompanyId.FromOrdinal(1);
        var headers = new HeaderDictionary
        {
            [BusinessSessionHeaders.UserId] = expectedUserId.Value,
            [BusinessSessionHeaders.ActiveCompanyId] = expectedCompanyId.Value
        };

        var reader = new BusinessSessionRequestReader();

        var success = reader.TryRead(headers, out var context, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(context);
        Assert.Equal((object?)expectedUserId, (object?)context.UserId);
        Assert.Equal((object?)expectedCompanyId, (object?)context.ActiveCompanyId);
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
    public void TryRead_ReturnsError_WhenHeaderIsNotValidId()
    {
        var headers = new HeaderDictionary
        {
            [BusinessSessionHeaders.UserId] = "not-an-id",
            [BusinessSessionHeaders.ActiveCompanyId] = CompanyId.FromOrdinal(1).Value
        };

        var reader = new BusinessSessionRequestReader();

        var success = reader.TryRead(headers, out _, out var error);

        Assert.False(success);
        Assert.Equal($"Header '{BusinessSessionHeaders.UserId}' must be a valid user id.", error);
    }
}
