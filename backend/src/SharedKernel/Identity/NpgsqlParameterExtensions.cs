using Npgsql;

namespace SharedKernel.Identity;

/// <summary>
/// AddWithValue overloads so call sites can pass typed value objects
/// (CompanyId, UserId, EntityNumber) directly without remembering to
/// unwrap to <c>.Value</c>. Without these overloads Npgsql throws
/// <c>InvalidCastException: Writing values of 'SharedKernel.Identity.UserId'
/// is not supported for parameters having no NpgsqlDbType or DataTypeName</c>.
///
/// The struct's Value field stores the canonical text form ("C000001",
/// "U000001", "EN20260000001"), which Npgsql persists into the matching
/// <c>char(N)</c> column without further coercion.
///
/// `SharedKernel.Identity` is a Directory.Build.props global Using, so
/// these extensions resolve at every call site automatically.
/// </summary>
public static class NpgsqlParameterExtensions
{
    public static NpgsqlParameter AddWithValue(this NpgsqlParameterCollection parameters, string parameterName, CompanyId value) =>
        parameters.AddWithValue(parameterName, (object?)value.Value ?? DBNull.Value);

    public static NpgsqlParameter AddWithValue(this NpgsqlParameterCollection parameters, string parameterName, CompanyId? value) =>
        parameters.AddWithValue(parameterName, value.HasValue ? (object?)value.Value.Value ?? DBNull.Value : DBNull.Value);

    public static NpgsqlParameter AddWithValue(this NpgsqlParameterCollection parameters, string parameterName, UserId value) =>
        parameters.AddWithValue(parameterName, (object?)value.Value ?? DBNull.Value);

    public static NpgsqlParameter AddWithValue(this NpgsqlParameterCollection parameters, string parameterName, UserId? value) =>
        parameters.AddWithValue(parameterName, value.HasValue ? (object?)value.Value.Value ?? DBNull.Value : DBNull.Value);

    public static NpgsqlParameter AddWithValue(this NpgsqlParameterCollection parameters, string parameterName, EntityNumber value) =>
        parameters.AddWithValue(parameterName, (object?)value.Value ?? DBNull.Value);

    public static NpgsqlParameter AddWithValue(this NpgsqlParameterCollection parameters, string parameterName, EntityNumber? value) =>
        parameters.AddWithValue(parameterName, value.HasValue ? (object?)value.Value.Value ?? DBNull.Value : DBNull.Value);
}
