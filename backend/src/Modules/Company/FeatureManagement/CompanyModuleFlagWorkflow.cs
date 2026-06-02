using System.Collections.Concurrent;

namespace Modules.Company.FeatureManagement;

public sealed class CompanyModuleFlagWorkflow : ICompanyModuleFlagWorkflow
{
    // Short TTL: the gate is hot-path on every business API call once
    // Task / future modules wire in. 60 s keeps the DB read cost flat
    // without making toggles take "noticeable" effect — a SysAdmin who
    // flips a switch waits at most one TTL window for the API to start
    // returning 404. Write-side invalidate makes this near-instant
    // within the same process.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly ICompanyModuleFlagStore _store;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();
    private readonly Func<DateTimeOffset> _now;

    public CompanyModuleFlagWorkflow(ICompanyModuleFlagStore store)
        : this(store, static () => DateTimeOffset.UtcNow)
    {
    }

    // Test seam: injectable clock so unit tests can advance time
    // without sleeping. Production code calls the single-argument
    // constructor above; this overload is wide-visibility because the
    // test assembly lives in a separate, unsigned project.
    public CompanyModuleFlagWorkflow(
        ICompanyModuleFlagStore store,
        Func<DateTimeOffset> nowProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _now = nowProvider ?? throw new ArgumentNullException(nameof(nowProvider));
    }

    public IReadOnlyList<CompanyModuleFlagOption> GetAvailableModules() =>
        CompanyModuleFlagCatalog.Options;

    public Task<IReadOnlyList<CompanyModuleFlagSummary>> ListAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to list module flags.");
        }

        return _store.ListAsync(companyId, cancellationToken);
    }

    public async Task<bool> IsEnabledAsync(
        CompanyId companyId,
        string moduleKey,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to check a module flag.");
        }

        // Unknown keys are always "off" — we never want to accidentally
        // open a gate for a typo'd module key.
        if (!CompanyModuleFlagCatalog.IsKnown(moduleKey))
        {
            return false;
        }

        var normalized = CompanyModuleFlagCatalog.NormalizeKey(moduleKey);
        var cacheKey = new CacheKey(companyId, normalized);
        var now = _now();

        if (_cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAtUtc > now)
        {
            return entry.Enabled;
        }

        var enabled = await _store.IsEnabledAsync(companyId, normalized, cancellationToken);
        _cache[cacheKey] = new CacheEntry(enabled, now + CacheTtl);
        return enabled;
    }

    public async Task<CompanyModuleFlagAccessStatus> GetAccessStatusAsync(
        CompanyId companyId,
        string moduleKey,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to check a module flag.");
        }

        if (!CompanyModuleFlagCatalog.IsKnown(moduleKey))
        {
            return new CompanyModuleFlagAccessStatus(
                companyId,
                moduleKey,
                Enabled: false,
                AccessExpiresAtUtc: null,
                IsExpired: false);
        }

        var normalized = CompanyModuleFlagCatalog.NormalizeKey(moduleKey);
        return await _store.GetAccessStatusAsync(
            companyId,
            normalized,
            _now(),
            cancellationToken);
    }

    public async Task<CompanyModuleFlagUpdateResult> SetEnabledFromSysAdminAsync(
        CompanyId companyId,
        string moduleKey,
        bool enabled,
        string reason,
        UserId? sysAdminAccountId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to set a module flag.");
        }

        var normalized = CompanyModuleFlagCatalog.NormalizeKey(moduleKey);
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? $"SysAdmin set module '{normalized}' enabled={enabled}."
            : reason.Trim();

        var result = await _store.SetEnabledAsync(
            companyId,
            normalized,
            enabled,
            normalizedReason,
            actorType: "sysadmin",
            actorUserId: sysAdminAccountId,
            accessExpiresAtUtc: null,
            forceAuditOnNoChange: false,
            cancellationToken);

        // Eager invalidation so a freshly toggled value is visible
        // inside the same process before the TTL would have refreshed.
        _cache[new CacheKey(companyId, normalized)] = new CacheEntry(
            result.Flag.Enabled,
            _now() + CacheTtl);

        return result;
    }

    public async Task<CompanyModuleFlagUpdateResult> SetEnabledFromOwnerAsync(
        CompanyId companyId,
        string moduleKey,
        bool enabled,
        string reason,
        UserId actorUserId,
        CancellationToken cancellationToken)
    {
        if (companyId.Value is null)
        {
            throw new InvalidOperationException("Company context is required to set a module flag.");
        }
        if (actorUserId.Value is null)
        {
            throw new InvalidOperationException("Actor user id is required for the business-side toggle.");
        }
        if (!enabled)
        {
            throw new InvalidOperationException("Business-side module access cannot be disabled from the owner toggle.");
        }

        var normalized = CompanyModuleFlagCatalog.NormalizeKey(moduleKey);
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? $"Owner set module '{normalized}' enabled={enabled}."
            : reason.Trim();
        var accessExpiresAtUtc = _now().AddDays(CompanyModuleFlagCatalog.DefaultSelfServiceAccessDays);

        var result = await _store.SetEnabledAsync(
            companyId,
            normalized,
            enabled,
            normalizedReason,
            actorType: "user",
            actorUserId: actorUserId,
            accessExpiresAtUtc,
            forceAuditOnNoChange: false,
            cancellationToken);

        _cache[new CacheKey(companyId, normalized)] = new CacheEntry(
            result.Flag.Enabled,
            _now() + CacheTtl);

        return result;
    }

    private readonly record struct CacheKey(CompanyId CompanyId, string ModuleKey);

    private readonly record struct CacheEntry(bool Enabled, DateTimeOffset ExpiresAtUtc);
}
