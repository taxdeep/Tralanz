using Xunit;

// Tests in this project share the same Postgres database (CompanyAccess
// memberships, audit_logs, companies, users tables). Multiple tests use
// overlapping FromOrdinal(101..) seeds, so running them in parallel
// triggers users_pkey / companies_pkey collisions. Match the pattern
// already in Tests/GL/AssemblyInfo.cs and serialize the suite.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
