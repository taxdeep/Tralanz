using Citus.Platform.Core.Metadata;
using Citus.Platform.Core.Modules;

namespace Citus.Platform.Core.BuiltIn;

public static class CitusPlatformKernel
{
    public static IReadOnlyList<CoreEntityDefinition> GetBuiltInEntities() =>
    [
        Entity(
            id: Guid.Parse("0bf3d4bd-255c-432d-9343-5dbe4ace2d10"),
            moduleKey: PlatformModuleKeys.Platform,
            name: "users",
            label: "User",
            labelPlural: "Users",
            description: "Platform identity records for every human or automation principal.",
            storageTable: "users",
            companyScoped: false,
            systemScoped: true,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("email", "Email", "text", "email", required: true, searchable: true, maxLength: 320),
                Field("username", "Username", "text", "username", searchable: true, maxLength: 120),
                Field("password_hash", "Password Hash", "text", "password_hash", required: true),
                Field("is_active", "Active", "boolean", "is_active", required: true),
                Field("created_at", "Created At", "datetime", "created_at", required: true, auditable: true, system: true),
                Field("updated_at", "Updated At", "datetime", "updated_at", required: true, auditable: true, system: true)
            ],
            permissions: SystemAdminPermissions()),
        Entity(
            id: Guid.Parse("de4b0df0-5b59-4898-b7fe-4687917f7e36"),
            moduleKey: PlatformModuleKeys.Platform,
            name: "companies",
            label: "Company",
            labelPlural: "Companies",
            description: "Tenant boundary and operating currency for every business using Citus.",
            storageTable: "companies",
            companyScoped: false,
            systemScoped: true,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("legal_name", "Legal Name", "text", "legal_name", required: true, searchable: true, maxLength: 240),
                Field("base_currency_code", "Base Currency", "text", "base_currency_code", required: true, searchable: true, maxLength: 3),
                Field("multi_currency_enabled", "Multi Currency Enabled", "boolean", "multi_currency_enabled", required: true),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40),
                Field("created_at", "Created At", "datetime", "created_at", required: true, auditable: true, system: true),
                Field("updated_at", "Updated At", "datetime", "updated_at", required: true, auditable: true, system: true)
            ],
            permissions: SystemAdminPermissions()),
        Entity(
            id: Guid.Parse("fc7338f1-840c-4b6f-a5ce-7677213c2233"),
            moduleKey: PlatformModuleKeys.Platform,
            name: "company_memberships",
            label: "Company Membership",
            labelPlural: "Company Memberships",
            description: "Membership and role assignment linking users into company workspaces.",
            storageTable: "company_memberships",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("user_id", "User", "uuid", "user_id", required: true, searchable: true, system: true),
                Field("role", "Role", "text", "role", required: true, searchable: true, maxLength: 40),
                Field("permissions", "Permissions", "jsonb", "permissions", required: true),
                Field("is_active", "Active", "boolean", "is_active", required: true),
                Field("created_at", "Created At", "datetime", "created_at", required: true, auditable: true, system: true),
                Field("updated_at", "Updated At", "datetime", "updated_at", required: true, auditable: true, system: true)
            ],
            permissions: CompanyAdminPermissions()),
        Entity(
            id: Guid.Parse("c6295b86-980c-4c2c-a090-30d1559a8617"),
            moduleKey: PlatformModuleKeys.Platform,
            name: "business_sessions",
            label: "Business Session",
            labelPlural: "Business Sessions",
            description: "Authenticated session tokens with an active company context.",
            storageTable: "business_sessions",
            companyScoped: false,
            systemScoped: true,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("token_hash", "Token Hash", "text", "token_hash", required: true, searchable: true, system: true),
                Field("user_id", "User", "uuid", "user_id", required: true, searchable: true, system: true),
                Field("active_company_id", "Active Company", "uuid", "active_company_id", required: true, searchable: true, system: true),
                Field("expires_at", "Expires At", "datetime", "expires_at", required: true, auditable: true),
                Field("created_at", "Created At", "datetime", "created_at", required: true, auditable: true, system: true)
            ],
            permissions: SystemAdminPermissions()),
        Entity(
            id: Guid.Parse("a2794fc1-6221-4c9f-abf2-f32e4ff6fe5b"),
            moduleKey: PlatformModuleKeys.SysAdmin,
            name: "platform_modules",
            label: "Platform Module",
            labelPlural: "Platform Modules",
            description: "Registered bounded-context modules that compose the Citus product surface.",
            storageTable: "platform_modules",
            companyScoped: false,
            systemScoped: true,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("module_key", "Module Key", "text", "module_key", required: true, searchable: true, system: true, maxLength: 80),
                Field("json", "Json", "jsonb", "json", required: true, system: true),
                Field("created_at", "Created At", "datetime", "created_at", required: true, auditable: true, system: true),
                Field("updated_at", "Updated At", "datetime", "updated_at", required: true, auditable: true, system: true)
            ],
            permissions: SystemAdminPermissions()),
        Entity(
            id: Guid.Parse("873cdbf4-7609-4634-b667-4e14e67a96d9"),
            moduleKey: PlatformModuleKeys.SysAdmin,
            name: "platform_entities",
            label: "Platform Entity",
            labelPlural: "Platform Entities",
            description: "Metadata registry describing which entities exist, where they live, and who can operate them.",
            storageTable: "platform_entities",
            companyScoped: false,
            systemScoped: true,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("entity_name", "Entity Name", "text", "entity_name", required: true, searchable: true, system: true, maxLength: 80),
                Field("module_key", "Module Key", "text", "module_key", required: true, searchable: true, system: true, maxLength: 80),
                Field("storage_table", "Storage Table", "text", "storage_table", required: true, searchable: true, system: true, maxLength: 120),
                Field("json", "Json", "jsonb", "json", required: true, system: true),
                Field("created_at", "Created At", "datetime", "created_at", required: true, auditable: true, system: true),
                Field("updated_at", "Updated At", "datetime", "updated_at", required: true, auditable: true, system: true)
            ],
            permissions: SystemAdminPermissions()),
        Entity(
            id: Guid.Parse("d5a2e53d-eb30-4dc8-88e5-9a395370405f"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "accounts",
            label: "Account",
            labelPlural: "Accounts",
            description: "Chart-of-accounts metadata and routing for posting.",
            storageTable: "accounts",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("code", "Code", "text", "code", required: true, searchable: true, maxLength: 40),
                Field("name", "Name", "text", "name", required: true, searchable: true, maxLength: 240),
                Field("root_type", "Root Type", "text", "root_type", required: true, searchable: true, maxLength: 40),
                Field("system_role", "System Role", "text", "system_role", searchable: true, maxLength: 80),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40)
            ],
            permissions: AccountingPermissions()),
        Entity(
            id: Guid.Parse("55a94ef8-b268-4f8c-bd6f-8a3106d8526a"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "tax_codes",
            label: "Tax Code",
            labelPlural: "Tax Codes",
            description: "Company tax definitions used by invoice and bill routing.",
            storageTable: "tax_codes",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("code", "Code", "text", "code", required: true, searchable: true, maxLength: 40),
                Field("name", "Name", "text", "name", required: true, searchable: true, maxLength: 240),
                Field("rate", "Rate", "numeric", "rate", required: true),
                Field("is_active", "Active", "boolean", "is_active", required: true)
            ],
            permissions: AccountingPermissions()),
        Entity(
            id: Guid.Parse("16ef9d96-5bd0-48c5-b1d7-9e113814cb25"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "customers",
            label: "Customer",
            labelPlural: "Customers",
            description: "Accounts receivable counterparties.",
            storageTable: "customers",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("display_name", "Display Name", "text", "display_name", required: true, searchable: true, maxLength: 240),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40)
            ],
            permissions: AccountingPermissions()),
        Entity(
            id: Guid.Parse("ed6c2913-e0d5-4cac-a0d2-51432be7f4d5"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "vendors",
            label: "Vendor",
            labelPlural: "Vendors",
            description: "Accounts payable counterparties.",
            storageTable: "vendors",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("display_name", "Display Name", "text", "display_name", required: true, searchable: true, maxLength: 240),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40)
            ],
            permissions: AccountingPermissions()),
        Entity(
            id: Guid.Parse("53ad98cf-2a41-4f93-a4e1-2a512ffc6cef"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "invoices",
            label: "Invoice",
            labelPlural: "Invoices",
            description: "Sales source documents that must post through the accounting engine.",
            storageTable: "invoices",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("invoice_number", "Invoice Number", "text", "invoice_number", required: true, searchable: true, maxLength: 40),
                Field("customer_id", "Customer", "uuid", "customer_id", required: true, searchable: true),
                Field("invoice_date", "Invoice Date", "date", "invoice_date", required: true, auditable: true),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40),
                Field("total_amount", "Total Amount", "numeric", "total_amount", required: true)
            ],
            permissions: AccountingPermissions()),
        Entity(
            id: Guid.Parse("dcce0af6-d286-4de5-b84f-3cce8344ef41"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "bills",
            label: "Bill",
            labelPlural: "Bills",
            description: "Purchasing source documents that must post through the accounting engine.",
            storageTable: "bills",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("bill_number", "Bill Number", "text", "bill_number", required: true, searchable: true, maxLength: 40),
                Field("vendor_id", "Vendor", "uuid", "vendor_id", required: true, searchable: true),
                Field("bill_date", "Bill Date", "date", "bill_date", required: true, auditable: true),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40),
                Field("total_amount", "Total Amount", "numeric", "total_amount", required: true)
            ],
            permissions: AccountingPermissions()),
        Entity(
            id: Guid.Parse("82d01d04-e53d-4648-8b51-d8c763661a67"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "journal_entries",
            label: "Journal Entry",
            labelPlural: "Journal Entries",
            description: "Authoritative posted accounting truth emitted by the posting engine.",
            storageTable: "journal_entries",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("display_number", "Display Number", "text", "display_number", required: true, searchable: true, maxLength: 40),
                Field("source_type", "Source Type", "text", "source_type", required: true, searchable: true, maxLength: 80),
                Field("posting_date", "Posting Date", "date", "posting_date", required: true, auditable: true),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40)
            ],
            permissions: AccountingReadMostlyPermissions()),
        Entity(
            id: Guid.Parse("df68d31a-30ca-4601-bd7a-bf699183c26e"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "ar_open_items",
            label: "AR Open Item",
            labelPlural: "AR Open Items",
            description: "Open receivables control rows that drive settlement and FX revaluation.",
            storageTable: "ar_open_items",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("customer_id", "Customer", "uuid", "customer_id", required: true, searchable: true),
                Field("source_document_type", "Source Type", "text", "source_document_type", required: true, searchable: true, maxLength: 80),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40),
                Field("open_amount_tx", "Open Amount Tx", "numeric", "open_amount_tx", required: true),
                Field("open_amount_base", "Open Amount Base", "numeric", "open_amount_base", required: true)
            ],
            permissions: AccountingReadMostlyPermissions()),
        Entity(
            id: Guid.Parse("d12e7c9c-9c69-48ca-b83a-f921628852fe"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "ap_open_items",
            label: "AP Open Item",
            labelPlural: "AP Open Items",
            description: "Open payables control rows that drive settlement and FX revaluation.",
            storageTable: "ap_open_items",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("vendor_id", "Vendor", "uuid", "vendor_id", required: true, searchable: true),
                Field("source_document_type", "Source Type", "text", "source_document_type", required: true, searchable: true, maxLength: 80),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40),
                Field("open_amount_tx", "Open Amount Tx", "numeric", "open_amount_tx", required: true),
                Field("open_amount_base", "Open Amount Base", "numeric", "open_amount_base", required: true)
            ],
            permissions: AccountingReadMostlyPermissions()),
        Entity(
            id: Guid.Parse("ca5ce1b4-f392-4ccf-87ad-746285e7187b"),
            moduleKey: PlatformModuleKeys.Accounting,
            name: "fx_revaluation_batches",
            label: "FX Revaluation Batch",
            labelPlural: "FX Revaluation Batches",
            description: "Period-end foreign currency remeasurement batches and unwind chain anchors.",
            storageTable: "fx_revaluation_batches",
            companyScoped: true,
            systemScoped: false,
            fields:
            [
                Field("id", "Id", "uuid", "id", required: true, searchable: true, system: true),
                Field("company_id", "Company", "uuid", "company_id", required: true, searchable: true, system: true),
                Field("entity_number", "Entity Number", "text", "entity_number", required: true, searchable: true, system: true, maxLength: 40),
                Field("display_number", "Display Number", "text", "display_number", required: true, searchable: true, maxLength: 40),
                Field("revaluation_date", "Revaluation Date", "date", "revaluation_date", required: true, auditable: true),
                Field("status", "Status", "text", "status", required: true, searchable: true, maxLength: 40),
                Field("batch_kind", "Batch Kind", "text", "batch_kind", required: true, searchable: true, maxLength: 40)
            ],
            permissions: AccountingPermissions())
    ];

    public static IReadOnlyList<PlatformModuleManifest> GetBuiltInModules() =>
    [
        Module(
            id: Guid.Parse("5f59462a-c262-4cee-90ca-f39a29ea67c6"),
            key: PlatformModuleKeys.Platform,
            name: "Citus Platform",
            description: "Shared kernel for identity, tenant isolation, and platform-level entity metadata.",
            routePrefix: "/core",
            isSystemModule: true,
            capabilities: ["tenant-isolation", "identity", "entity-metadata", "module-registry"],
            entityNames: EntityNamesFor(GetBuiltInEntities(), PlatformModuleKeys.Platform)),
        Module(
            id: Guid.Parse("1e962b0c-ac00-476d-ba52-fa09c8f4efcc"),
            key: PlatformModuleKeys.Accounting,
            name: "Citus Accounting",
            description: "Posting-engine-driven accounting bounded context registered inside the shared platform kernel.",
            routePrefix: "/accounting",
            isSystemModule: false,
            capabilities: ["posting-engine", "multi-currency", "open-item-control", "fx-revaluation"],
            entityNames: EntityNamesFor(GetBuiltInEntities(), PlatformModuleKeys.Accounting)),
        Module(
            id: Guid.Parse("b6d72cca-b5b4-4ae8-a8aa-90aa2e1bcd4f"),
            key: PlatformModuleKeys.SysAdmin,
            name: "Citus SysAdmin",
            description: "Administrative control surface for bootstrapping and governing the platform kernel.",
            routePrefix: "/",
            isSystemModule: true,
            capabilities: ["bootstrap", "metadata-governance", "module-observability"],
            entityNames: EntityNamesFor(GetBuiltInEntities(), PlatformModuleKeys.SysAdmin))
    ];

    private static IReadOnlyList<string> EntityNamesFor(
        IReadOnlyList<CoreEntityDefinition> entities,
        string moduleKey) =>
        entities
            .Where(entity => entity.ModuleKey == moduleKey)
            .Select(entity => entity.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static CoreEntityDefinition Entity(
        Guid id,
        string moduleKey,
        string name,
        string label,
        string labelPlural,
        string description,
        string storageTable,
        bool companyScoped,
        bool systemScoped,
        IReadOnlyList<CoreFieldDefinition> fields,
        CoreEntityPermissionSet permissions) =>
        new()
        {
            Id = id,
            ModuleKey = moduleKey,
            Name = name,
            Label = label,
            LabelPlural = labelPlural,
            Description = description,
            StorageTable = storageTable,
            CompanyScoped = companyScoped,
            SystemScoped = systemScoped,
            Fields = fields,
            Permissions = permissions
        };

    private static PlatformModuleManifest Module(
        Guid id,
        string key,
        string name,
        string description,
        string routePrefix,
        bool isSystemModule,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> entityNames) =>
        new()
        {
            Id = id,
            Key = key,
            Name = name,
            Description = description,
            RoutePrefix = routePrefix,
            IsSystemModule = isSystemModule,
            Capabilities = capabilities,
            EntityNames = entityNames
        };

    private static CoreFieldDefinition Field(
        string name,
        string label,
        string fieldType,
        string sourceColumn,
        bool required = false,
        bool searchable = false,
        bool auditable = false,
        bool system = false,
        int? maxLength = null,
        string description = "") =>
        new()
        {
            Name = name,
            Label = label,
            FieldType = fieldType,
            SourceColumn = sourceColumn,
            Description = description,
            Required = required,
            Searchable = searchable,
            Auditable = auditable,
            System = system,
            MaxLength = maxLength
        };

    private static CoreEntityPermissionSet SystemAdminPermissions() =>
        new()
        {
            Create = ["sysadmin"],
            Read = ["sysadmin"],
            Update = ["sysadmin"],
            Delete = ["sysadmin"]
        };

    private static CoreEntityPermissionSet CompanyAdminPermissions() =>
        new()
        {
            Create = ["sysadmin", "owner"],
            Read = ["sysadmin", "owner"],
            Update = ["sysadmin", "owner"],
            Delete = ["sysadmin", "owner"]
        };

    private static CoreEntityPermissionSet AccountingPermissions() =>
        new()
        {
            Create = ["sysadmin", "owner", "controller", "accountant"],
            Read = ["sysadmin", "owner", "controller", "accountant", "auditor"],
            Update = ["sysadmin", "owner", "controller", "accountant"],
            Delete = ["sysadmin", "owner", "controller"]
        };

    private static CoreEntityPermissionSet AccountingReadMostlyPermissions() =>
        new()
        {
            Create = ["sysadmin", "owner", "controller"],
            Read = ["sysadmin", "owner", "controller", "accountant", "auditor"],
            Update = ["sysadmin", "owner", "controller"],
            Delete = ["sysadmin", "owner"]
        };
}
