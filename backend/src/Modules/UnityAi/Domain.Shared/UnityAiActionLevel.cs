namespace Citus.Modules.UnityAi.Domain.Shared;

/// <summary>
/// Declared capability tier of a unityAI surface. Higher levels require
/// explicit company opt-in, stricter feature flags, and stricter audit.
/// MVP only ships levels 0..2; levels 3..4 are reserved.
/// </summary>
public enum UnityAiActionLevel
{
    /// <summary>Level 0 — explain, summarize, answer questions only.</summary>
    ReadOnly = 0,
    /// <summary>Level 1 — recommend vendor / account / tax / report / task.</summary>
    SuggestOnly = 1,
    /// <summary>Level 2 — create draft (invoice / bill / expense / journal-entry draft); never posts.</summary>
    CreateDraft = 2,
    /// <summary>Level 3 — generate posting preview; user must confirm before commit. Future.</summary>
    PreparePosting = 3,
    /// <summary>Level 4 — auto-post under explicit company policy. Out of V1.</summary>
    AutoPostWithPolicy = 4,
}
