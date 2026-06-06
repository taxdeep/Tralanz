// Exposes the top-level-statements entry point as a public partial class so
// WebApplicationFactory<Program> in Citus.Accounting.Api.Tests can boot the
// real host in-memory (host smoke + route-snapshot tests). Mirrors the
// established Citus.SysAdmin.Api/Program.Public.cs pattern.
public partial class Program;
