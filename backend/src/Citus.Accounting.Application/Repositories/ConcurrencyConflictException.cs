namespace Citus.Accounting.Application.Repositories;

/// <summary>
/// Thrown by document repositories when an UPDATE was rejected because
/// the row's <c>updated_at</c> on disk no longer matches the timestamp
/// the caller last observed. Routes catch this and surface HTTP 409 so
/// the operator can refresh and re-apply their changes instead of
/// silently overwriting another session's edits.
/// </summary>
/// <remarks>
/// Pattern: GET returns the row with its <c>updated_at</c>; the editor
/// holds onto that value and includes it on the next PUT (as
/// <c>ExpectedUpdatedAt</c>). The repository's UPDATE adds
/// <c>AND updated_at = @expected_updated_at</c>; zero rows affected
/// while the row still exists in the right status means another
/// session moved the timestamp. Distinguishing this from "row was
/// posted / deleted" lets the UI explain *why* the save was rejected.
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
