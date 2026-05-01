namespace Citus.Modules.UnitySearch.Domain.Shared;

public static class SearchScopeContext
{
    public const string GlobalTopbar = "global.topbar";
    public const string GlobalTransactions = "global.transactions";
    public const string InventoryShipmentCustomerPicker = "inventory.shipment_customer_picker";
    public const string InventoryShipmentItemPicker = "inventory.shipment_item_picker";
    public const string InventoryShipmentWarehousePicker = "inventory.shipment_warehouse_picker";
    public const string InventoryTransferItemPicker = "inventory.transfer_item_picker";
    public const string InventoryTransferWarehousePicker = "inventory.transfer_warehouse_picker";
    public const string InventoryAdjustmentItemPicker = "inventory.adjustment_item_picker";
    public const string InventoryAdjustmentWarehousePicker = "inventory.adjustment_warehouse_picker";
    public const string SalesCustomerPicker = "sales.customer_picker";
    public const string SalesItemServicePicker = "sales.item_service_picker";
    public const string AccountPicker = "account.picker";
    public const string QuoteCustomerPicker = "quote.customer_picker";
    public const string QuoteProductServicePicker = "quote.product_service_picker";
    public const string QuoteInventoryItemPicker = "quote.inventory_item_picker";
    public const string SalesOrderCustomerPicker = "sales_order.customer_picker";
    public const string SalesOrderProductServicePicker = "sales_order.product_service_picker";
    public const string SalesOrderInventoryItemPicker = "sales_order.inventory_item_picker";
    public const string PurchaseOrderVendorPicker = "purchase_order.vendor_picker";
    public const string PurchaseOrderInventoryItemPicker = "purchase_order.inventory_item_picker";
    public const string InvoiceCustomerPicker = "invoice.customer_picker";
    public const string InvoiceItemPicker = "invoice.item_picker";
    public const string BillVendorPicker = "bill.vendor_picker";
    public const string JournalEntryAccountPicker = "journal_entry.account_picker";

    /// <summary>
    /// Per-line counterparty picker on the journal-entry surface. Spans
    /// both customers and vendors so a journal line can reference either
    /// a payable or a receivable counterparty without the user picking
    /// the document type up front.
    /// </summary>
    public const string JournalEntryNamePicker = "journal_entry.name_picker";
}
