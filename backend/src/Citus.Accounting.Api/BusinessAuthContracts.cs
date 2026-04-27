using Citus.Ui.Shared.Business;

namespace Citus.Accounting.Api;

/// <summary>
/// Wire-shape for <c>POST /auth/login</c>. Field names match what
/// <see cref="Citus.Business.Blazor.Services.BusinessAuthenticationClient.SignInRequest"/>
/// posts so System.Text.Json round-trips without a custom converter.
/// </summary>
internal sealed class BusinessSignInRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Wire-shape for the <c>POST /auth/login</c> success body. Mirrors
/// <see cref="Citus.Business.Blazor.Services.BusinessAuthenticationClient.SignInResponse"/>
/// — the Blazor client deserialises this directly via
/// <c>response.Content.ReadFromJsonAsync&lt;SignInResponse&gt;()</c>, so the
/// shape must stay in sync.
/// </summary>
internal sealed class BusinessSignInResponse
{
    public bool Succeeded { get; set; }

    public string SessionToken { get; set; } = string.Empty;

    public BusinessAuthSessionSummary Session { get; set; } = new();

    public string Message { get; set; } = string.Empty;

    public bool IsBootstrap { get; set; }
}
