using Citus.Modules.UnitySearch.Blazor;

namespace Citus.Business.Blazor.Components.Shared;

/// <summary>
/// Strongly typed payload emitted by <c>UserPicker.OnUserSelected</c>.
/// The picker hashes the char(7) platform user_id into a synthetic
/// uuid for the search fabric; this record carries the real
/// <see cref="UserId"/> alongside the display text so callers stay
/// clear of the encoding detail. The full
/// <see cref="UnitySearchPickerOption"/> is still surfaced for
/// callers that want extra projected metadata (role, email) without
/// re-parsing the metadata JSON.
/// </summary>
public sealed record UserPickerSelection(UserId UserId, string DisplayText, UnitySearchPickerOption Source);
