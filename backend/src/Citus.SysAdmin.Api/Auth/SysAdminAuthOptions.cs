namespace Citus.SysAdmin.Api.Auth;

public sealed class SysAdminAuthOptions
{
    public const string SectionName = "SysAdminAuthentication";

    public int SessionHours { get; set; } = 12;

    public BootstrapOptions Bootstrap { get; set; } = new();

    public sealed class BootstrapOptions
    {
        /// <summary>
        /// H14: the password literal used when no override is supplied. Kept
        /// in code so the startup guard can refuse to seed a non-development
        /// host that still carries it.
        /// </summary>
        public const string DefaultDevPassword = "change-me-now";

        public bool Enabled { get; set; } = true;

        public bool AllowInNonDevelopment { get; set; }

        public string Email { get; set; } = "sysadmin@citus.local";

        public string DisplayName { get; set; } = "Platform Administrator";

        public string Password { get; set; } = DefaultDevPassword;

        public bool IsActive(bool isDevelopment) =>
            Enabled && (isDevelopment || AllowInNonDevelopment);

        /// <summary>
        /// H14: true when this options object would seed a non-development
        /// host with the well-known default password. Caller (startup
        /// configuration in Program.cs) refuses to continue, forcing the
        /// operator to set SysAdminAuthentication__Bootstrap__Password
        /// before flipping AllowInNonDevelopment on.
        /// </summary>
        public bool IsDefaultPasswordInsecureForProduction(bool isDevelopment) =>
            IsActive(isDevelopment)
            && !isDevelopment
            && string.Equals(Password, DefaultDevPassword, StringComparison.Ordinal);
    }
}
