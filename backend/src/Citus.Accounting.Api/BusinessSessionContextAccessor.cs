namespace Citus.Accounting.Api;

public sealed class BusinessSessionContextAccessor
{
    public BusinessSessionContext? Current { get; private set; }

    public BusinessSessionResolution? CurrentResolution { get; private set; }

    public void Set(BusinessSessionContext context, BusinessSessionResolution? resolution)
    {
        Current = context;
        CurrentResolution = resolution;
    }
}
