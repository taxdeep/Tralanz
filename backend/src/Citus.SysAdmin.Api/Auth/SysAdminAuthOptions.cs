namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminAuthOptions
{
    public const string SectionName = "SysAdminAuthentication";

    public int SessionHours { get; set; } = 12;

    public BootstrapOptions Bootstrap { get; set; } = new();

    public sealed class BootstrapOptions
    {
        public bool Enabled { get; set; } = true;

        public bool AllowInNonDevelopment { get; set; }

        public string Email { get; set; } = "sysadmin@citus.local";

        public string DisplayName { get; set; } = "Platform Administrator";

        public string Password { get; set; } = "change-me-now";

        public bool IsActive(bool isDevelopment) =>
            Enabled && (isDevelopment || AllowInNonDevelopment);
    }
}
