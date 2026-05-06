using Xunit;

// Tests in this project share the same Postgres database (vendors, bills,
// vendor_credits, ap_open_items, journal_entries, audit_logs). Many tests
// reuse fixed seeds like UserId.FromOrdinal(1) for the actor row, so
// running them in parallel triggers users_pkey / vendors_pkey collisions
// and timeouts as connections fight for the same locked rows. Serialize
// the suite to match Tests/GL/AssemblyInfo.cs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
