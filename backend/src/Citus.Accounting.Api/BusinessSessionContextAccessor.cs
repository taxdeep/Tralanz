namespace Citus.Accounting.Api;

public sealed class BusinessSessionContextAccessor
{
    public BusinessSessionContext? Current { get; private set; }

    public void Set(BusinessSessionContext context)
    {
        Current = context;
    }
}
