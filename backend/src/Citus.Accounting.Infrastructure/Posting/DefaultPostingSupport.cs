using Citus.Accounting.Application.Abstractions;
using Citus.Accounting.Domain.Currencies;
using Citus.Accounting.Domain.Documents;
using Citus.Accounting.Domain.Journal;
using Citus.Accounting.Domain.Posting;
using Citus.Accounting.Infrastructure;

namespace Citus.Accounting.Infrastructure.Posting;

public sealed class DefaultPostingValidator : IPostingValidator
{
    public Task ValidateAsync(
        IPostingDocument document,
        PostingContext context,
        CancellationToken cancellationToken)
    {
        if (document.CompanyId != context.CompanyId)
        {
            throw new InvalidOperationException("Posting document company does not match the active company context.");
        }

        if (document.Status is not ("draft" or "posted"))
        {
            throw new InvalidOperationException(
                $"Document status '{document.Status}' cannot enter the posting engine.");
        }

        if (document is ManualJournalDocument manualJournal && !manualJournal.IsBalancedInTransactionCurrency())
        {
            throw new InvalidOperationException("Manual journal document is not balanced in transaction currency.");
        }

        if (document is InvoiceDocument invoice)
        {
            if (invoice.DocumentDate > invoice.DueDate)
            {
                throw new InvalidOperationException("Invoice due date cannot be earlier than invoice date.");
            }

            if (invoice.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Invoice must carry a positive total before posting.");
            }
        }

        if (document is SalesReceiptDocument salesReceipt)
        {
            if (salesReceipt.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Sales receipt must carry a positive total before posting.");
            }

            // Sales receipt is single-step (no submitted state); the
            // engine accepts only 'draft' here. Posted is rejected to
            // avoid double-posting via the same draft id.
            if (salesReceipt.Status != "draft")
            {
                throw new InvalidOperationException(
                    $"Sales receipt status '{salesReceipt.Status}' cannot enter the posting engine.");
            }
        }

        if (document is RefundReceiptDocument refundReceipt)
        {
            if (refundReceipt.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Refund receipt must carry a positive total before posting.");
            }

            if (refundReceipt.Status != "draft")
            {
                throw new InvalidOperationException(
                    $"Refund receipt status '{refundReceipt.Status}' cannot enter the posting engine.");
            }
        }

        if (document is BankTransferDocument bankTransfer)
        {
            if (bankTransfer.Amount <= 0m)
            {
                throw new InvalidOperationException("Bank transfer amount must be positive.");
            }
            if (bankTransfer.FromAccountId == bankTransfer.ToAccountId)
            {
                throw new InvalidOperationException("Bank transfer source and destination accounts must differ.");
            }
            if (bankTransfer.Status != "draft")
            {
                throw new InvalidOperationException(
                    $"Bank transfer status '{bankTransfer.Status}' cannot enter the posting engine.");
            }
        }

        if (document is BankDepositDocument bankDeposit)
        {
            if (bankDeposit.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Bank deposit total must be positive.");
            }
            if (bankDeposit.Items.Count == 0)
            {
                throw new InvalidOperationException("Bank deposit must contain at least one item.");
            }
            if (bankDeposit.DepositToAccountId == bankDeposit.UndepositedFundsAccountId)
            {
                throw new InvalidOperationException("Bank deposit destination account must differ from the Undeposited-Funds holding account.");
            }
            if (bankDeposit.Status != "draft")
            {
                throw new InvalidOperationException(
                    $"Bank deposit status '{bankDeposit.Status}' cannot enter the posting engine.");
            }
        }

        if (document is TaxReturnDocument taxReturn)
        {
            if (taxReturn.PeriodEnd < taxReturn.PeriodStart)
            {
                throw new InvalidOperationException("Tax return period end must be on or after period start.");
            }
            if (taxReturn.Status != "draft")
            {
                throw new InvalidOperationException(
                    $"Tax return status '{taxReturn.Status}' cannot enter the posting engine.");
            }
            // A zero-net return still legitimately posts (it locks
            // the period); we don't reject it, but we'll skip the
            // settlement row in the fragment builder.
        }

        if (document is CreditNoteDocument creditNote)
        {
            if (creditNote.DocumentDate > creditNote.DueDate)
            {
                throw new InvalidOperationException("Credit note due date cannot be earlier than credit note date.");
            }

            if (creditNote.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Credit note must carry a positive total before posting.");
            }
        }

        if (document is BillDocument bill)
        {
            if (bill.DocumentDate > bill.DueDate)
            {
                throw new InvalidOperationException("Bill due date cannot be earlier than bill date.");
            }

            if (bill.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Bill must carry a positive total before posting.");
            }
        }

        if (document is VendorCreditDocument vendorCredit)
        {
            if (vendorCredit.DocumentDate > vendorCredit.DueDate)
            {
                throw new InvalidOperationException("Vendor credit due date cannot be earlier than vendor credit date.");
            }

            if (vendorCredit.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Vendor credit must carry a positive total before posting.");
            }
        }

        if (document is CreditApplicationDocument creditApplication)
        {
            if (creditApplication.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Credit application must carry a positive total before posting.");
            }

            var appliedTotal = creditApplication.ApplicationLines.Sum(static line => line.AppliedAmount);
            if (appliedTotal != creditApplication.TotalAmount)
            {
                throw new InvalidOperationException("Credit application total must equal the sum of its application lines.");
            }

            if (creditApplication.TransactionCurrencyCode != creditApplication.BaseCurrencyCode &&
                creditApplication.ApplicationLines.Any(static line => line.RealizedFxAmountBase != 0m) &&
                (!creditApplication.RealizedFxGainAccountId.HasValue || !creditApplication.RealizedFxLossAccountId.HasValue))
            {
                throw new InvalidOperationException(
                    "Foreign-currency credit application with realized FX requires realized FX gain/loss accounts before posting.");
            }
        }

        if (document is ReceivePaymentDocument receivePayment)
        {
            if (receivePayment.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Receive payment must carry a positive total before posting.");
            }

            var appliedTotal = receivePayment.PaymentLines.Sum(static line => line.AppliedAmount);
            if (appliedTotal + receivePayment.ExtraDepositAmount != receivePayment.TotalAmount)
            {
                throw new InvalidOperationException("Receive payment total must equal the sum of its application lines plus extra customer deposit amount.");
            }

            var expectedBaseTotal = SettlementAmountMath.RoundBase(
                receivePayment.TotalAmount * (receivePayment.FxSnapshot?.Rate ?? 1m));
            var appliedBaseTotal = SettlementAmountMath.RoundBase(
                receivePayment.PaymentLines.Sum(static line => line.AppliedAmountBase) +
                receivePayment.ExtraDepositAmountBase);
            if (appliedBaseTotal != expectedBaseTotal)
            {
                throw new InvalidOperationException(
                    "Receive payment application base amounts do not reconcile to the document settlement base total.");
            }

            if (receivePayment.ExtraDepositAmount > 0m && !receivePayment.CustomerDepositAccountId.HasValue)
            {
                throw new InvalidOperationException(
                    "Receive payment overpayment requires a Customer Deposit account before posting.");
            }

            if (receivePayment.TransactionCurrencyCode != receivePayment.BaseCurrencyCode)
            {
                if (receivePayment.FxSnapshot is null)
                {
                    throw new InvalidOperationException(
                        "Foreign-currency receive payment requires a stored FX snapshot on the source document.");
                }

                if (!receivePayment.RealizedFxGainAccountId.HasValue || !receivePayment.RealizedFxLossAccountId.HasValue)
                {
                    throw new InvalidOperationException(
                        "Foreign-currency receive payment requires realized FX gain/loss accounts before posting.");
                }
            }
        }

        if (document is VendorCreditApplicationDocument vendorCreditApplication)
        {
            if (vendorCreditApplication.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Vendor credit application must carry a positive total before posting.");
            }

            var appliedTotal = vendorCreditApplication.ApplicationLines.Sum(static line => line.AppliedAmount);
            if (appliedTotal != vendorCreditApplication.TotalAmount)
            {
                throw new InvalidOperationException("Vendor credit application total must equal the sum of its application lines.");
            }

            if (vendorCreditApplication.TransactionCurrencyCode != vendorCreditApplication.BaseCurrencyCode &&
                vendorCreditApplication.ApplicationLines.Any(static line => line.RealizedFxAmountBase != 0m) &&
                (!vendorCreditApplication.RealizedFxGainAccountId.HasValue || !vendorCreditApplication.RealizedFxLossAccountId.HasValue))
            {
                throw new InvalidOperationException(
                    "Foreign-currency vendor credit application with realized FX requires realized FX gain/loss accounts before posting.");
            }
        }

        if (document is PayBillDocument payBill)
        {
            if (payBill.TotalAmount <= 0m)
            {
                throw new InvalidOperationException("Pay bill must carry a positive total before posting.");
            }

            var appliedTotal = payBill.PaymentLines.Sum(static line => line.AppliedAmount);
            if (appliedTotal != payBill.TotalAmount)
            {
                throw new InvalidOperationException("Pay bill total must equal the sum of its application lines.");
            }

            var expectedBaseTotal = SettlementAmountMath.RoundBase(
                payBill.TotalAmount * (payBill.FxSnapshot?.Rate ?? 1m));
            var appliedBaseTotal = SettlementAmountMath.RoundBase(
                payBill.PaymentLines.Sum(static line => line.AppliedAmountBase));
            if (appliedBaseTotal != expectedBaseTotal)
            {
                throw new InvalidOperationException(
                    "Pay bill application base amounts do not reconcile to the document settlement base total.");
            }

            if (payBill.TransactionCurrencyCode != payBill.BaseCurrencyCode)
            {
                if (payBill.FxSnapshot is null)
                {
                    throw new InvalidOperationException(
                        "Foreign-currency pay bill requires a stored FX snapshot on the source document.");
                }

                if (!payBill.RealizedFxGainAccountId.HasValue || !payBill.RealizedFxLossAccountId.HasValue)
                {
                    throw new InvalidOperationException(
                        "Foreign-currency pay bill requires realized FX gain/loss accounts before posting.");
                }
            }
        }

        if (document is FxRevaluationDocument fxRevaluation)
        {
            if (fxRevaluation.TransactionCurrencyCode == fxRevaluation.BaseCurrencyCode)
            {
                throw new InvalidOperationException("FX revaluation document must target a foreign transaction currency.");
            }

            if (fxRevaluation.RevaluationLines.Count == 0)
            {
                throw new InvalidOperationException("FX revaluation document must contain at least one revaluation line.");
            }

            if (fxRevaluation.FxSnapshot is null)
            {
                throw new InvalidOperationException("FX revaluation document requires a stored FX snapshot.");
            }

            if (fxRevaluation.UnrealizedFxGainAccountId == Guid.Empty || fxRevaluation.UnrealizedFxLossAccountId == Guid.Empty)
            {
                throw new InvalidOperationException("FX revaluation document requires unrealized FX gain/loss accounts.");
            }

            if (fxRevaluation.BatchKind == "next_period_unwind" && !fxRevaluation.ReversalOfDocumentId.HasValue)
            {
                throw new InvalidOperationException("FX revaluation unwind document must reference the posted batch it reverses.");
            }

            if (fxRevaluation.RevaluationLines.Any(static line => line.OffsetAccountId == Guid.Empty))
            {
                throw new InvalidOperationException("FX revaluation document contains a line without an unrealized FX offset account.");
            }
        }

        if (document is OpenItemAdjustmentDocument adjustment)
        {
            if (adjustment.AdjustmentLines.Count == 0)
            {
                throw new InvalidOperationException("Open item adjustment document must contain at least one line before posting.");
            }

            if (adjustment.AdjustmentLines.Any(static line => line.AdjustmentAmountTx <= 0m || line.AdjustmentAmountBase <= 0m))
            {
                throw new InvalidOperationException("Open item adjustment lines must carry positive transaction and base amounts.");
            }
        }

        if (document is ReceiptGrIrPostingDocument grIrPosting)
        {
            if (grIrPosting.TotalAmountBase <= 0m)
            {
                throw new InvalidOperationException("Receipt GR/IR posting must carry a positive base amount.");
            }

            if (grIrPosting.GrIrLines.Any(static line => line.AmountBase <= 0m))
            {
                throw new InvalidOperationException("Receipt GR/IR posting lines must carry positive base amounts.");
            }
        }

        if (document is ReceiptGrIrSettlementPostingDocument grIrSettlement)
        {
            if (grIrSettlement.TotalAmountBase <= 0m)
            {
                throw new InvalidOperationException("Receipt GR/IR settlement posting must carry a positive base amount.");
            }

            if (grIrSettlement.SettlementLines.Any(static line => line.GrIrAmountBase <= 0m))
            {
                throw new InvalidOperationException("Receipt GR/IR settlement posting lines must carry positive GR/IR base amounts.");
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class NullTaxEngine : ITaxEngine
{
    public Task<TaxComputationResult> CalculateAsync(
        IPostingDocument document,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new TaxComputationResult(Array.Empty<TaxComputationLine>(), 0m));
    }
}

public sealed class AccountingPostingFragmentBuilder : IPostingFragmentBuilder
{
    public Task<IReadOnlyList<PostingFragment>> BuildAsync(
        IPostingDocument document,
        TaxComputationResult taxResult,
        FxResolutionResult fxResult,
        CancellationToken cancellationToken)
    {
        return document switch
        {
            ManualJournalDocument manualJournal => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildManualJournalFragments(manualJournal, fxResult).AsReadOnly()),
            InvoiceDocument invoice => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildInvoiceFragments(invoice, fxResult).AsReadOnly()),
            SalesReceiptDocument salesReceipt => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildSalesReceiptFragments(salesReceipt, fxResult).AsReadOnly()),
            RefundReceiptDocument refundReceipt => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildRefundReceiptFragments(refundReceipt, fxResult).AsReadOnly()),
            BankTransferDocument bankTransfer => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildBankTransferFragments(bankTransfer, fxResult).AsReadOnly()),
            BankDepositDocument bankDeposit => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildBankDepositFragments(bankDeposit, fxResult).AsReadOnly()),
            TaxReturnDocument taxReturn => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildTaxReturnFragments(taxReturn, fxResult).AsReadOnly()),
            CreditNoteDocument creditNote => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildCreditNoteFragments(creditNote, fxResult).AsReadOnly()),
            BillDocument bill => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildBillFragments(bill, fxResult).AsReadOnly()),
            VendorCreditDocument vendorCredit => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildVendorCreditFragments(vendorCredit, fxResult).AsReadOnly()),
            CreditApplicationDocument creditApplication => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildCreditApplicationFragments(creditApplication).AsReadOnly()),
            ReceivePaymentDocument receivePayment => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildReceivePaymentFragments(receivePayment, fxResult).AsReadOnly()),
            VendorCreditApplicationDocument vendorCreditApplication => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildVendorCreditApplicationFragments(vendorCreditApplication).AsReadOnly()),
            PayBillDocument payBill => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildPayBillFragments(payBill, fxResult).AsReadOnly()),
            FxRevaluationDocument fxRevaluation => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildFxRevaluationFragments(fxRevaluation).AsReadOnly()),
            OpenItemAdjustmentDocument adjustment => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildOpenItemAdjustmentFragments(adjustment).AsReadOnly()),
            ReceiptGrIrPostingDocument grIrPosting => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildReceiptGrIrPostingFragments(grIrPosting).AsReadOnly()),
            ReceiptGrIrSettlementPostingDocument grIrSettlement => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildReceiptGrIrSettlementPostingFragments(grIrSettlement).AsReadOnly()),
            SalesIssueCogsPostingDocument cogsPosting => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildSalesIssueCogsPostingFragments(cogsPosting).AsReadOnly()),
            InvoiceDropShipCogsPostingDocument dropShipCogs => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildInvoiceDropShipCogsPostingFragments(dropShipCogs).AsReadOnly()),
            DropShipClearingWriteOffDocument dropShipWriteOff => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildDropShipClearingWriteOffFragments(dropShipWriteOff).AsReadOnly()),
            CustomerDepositPostingDocument deposit => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildCustomerDepositFragments(deposit).AsReadOnly()),
            CustomerDepositApplicationDocument depositApp => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildCustomerDepositApplicationFragments(depositApp).AsReadOnly()),
            ExpenseVoidPostingDocument expenseVoid => Task.FromResult<IReadOnlyList<PostingFragment>>(
                BuildExpenseVoidFragments(expenseVoid).AsReadOnly()),
            _ => throw new NotSupportedException(
                $"Document type '{document.SourceType}' is not yet supported by the fragment builder.")
        };
    }

    // H1: expense-void fragments are pre-flipped on the
    // ExpenseVoidPostingDocumentLine — the repository has already
    // read the original journal_entry_lines and swapped Dr↔Cr (and
    // TxDebit↔TxCredit). This builder maps each line straight to a
    // PostingFragment in the same shape; the engine's
    // EnsureJournalInvariants then re-validates the Dr=Cr balance on
    // both axes, providing the same safety net every other engine-
    // produced JE gets.
    private static List<PostingFragment> BuildExpenseVoidFragments(
        ExpenseVoidPostingDocument document)
    {
        var fragments = new List<PostingFragment>(document.VoidLines.Count);
        foreach (var line in document.VoidLines)
        {
            fragments.Add(new PostingFragment(
                line.AccountId,
                document.TransactionCurrencyCode,
                line.TxDebit,
                line.TxCredit,
                line.Debit,
                line.Credit,
                line.Description,
                TaxComponentType: null,
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                PostingRole: line.PostingRole,
                SourceLineNumber: line.SourceLineNumber));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildManualJournalFragments(
        ManualJournalDocument manualJournal,
        FxResolutionResult fxResult)
    {
        var fragments = manualJournal.JournalLines
            .Select(line =>
            {
                var baseDebit = Math.Round(line.TxDebit * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                var baseCredit = Math.Round(line.TxCredit * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);

                return new PostingFragment(
                    line.AccountId,
                    manualJournal.TransactionCurrencyCode,
                    line.TxDebit,
                    line.TxCredit,
                    baseDebit,
                    baseCredit,
                    line.Description,
                    PostingRole: "manual_journal",
                    SourceLineNumber: line.LineNumber);
            })
            .ToList();

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildInvoiceFragments(
        InvoiceDocument invoice,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();

        var arDebitBase = Math.Round(invoice.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            invoice.ReceivableAccountId,
            invoice.TransactionCurrencyCode,
            invoice.TotalAmount,
            0m,
            arDebitBase,
            0m,
            $"Accounts Receivable for invoice {invoice.DisplayNumber.Value}",
            ControlRole: "accounts_receivable",
            PartyId: invoice.PartyId,
            PostingRole: "control:accounts_receivable"));

        foreach (var line in invoice.InvoiceLines)
        {
            var baseRevenue = Math.Round(line.LineAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
            fragments.Add(new PostingFragment(
                line.RevenueAccountId,
                invoice.TransactionCurrencyCode,
                0m,
                line.LineAmount,
                0m,
                baseRevenue,
                BuildSourceLineDescription("Invoice revenue", invoice.DisplayNumber.Value, line.LineNumber, line.Description),
                PostingRole: "source_line:revenue",
                SourceLineNumber: line.LineNumber));

            if (line.TaxAmount > 0m)
            {
                var baseTax = Math.Round(line.TaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                fragments.Add(new PostingFragment(
                    line.PayableTaxAccountId!.Value,
                    invoice.TransactionCurrencyCode,
                    0m,
                    line.TaxAmount,
                    0m,
                    baseTax,
                    $"Sales tax for invoice {invoice.DisplayNumber.Value} line {line.LineNumber}",
                    TaxComponentType: "sales_tax_payable",
                    PostingRole: "tax:sales_tax_payable",
                    SourceLineNumber: line.LineNumber));
            }
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// Build the GL fragments for a Sales Receipt. Polarity is the
    /// inverse of the AR side of an Invoice: cash flows IN, so the
    /// deposit-to account is debited; revenue + tax-payable per line
    /// are credited.
    ///
    ///   Dr DepositToAccountId      = TotalAmount
    ///   Cr line.RevenueAccountId   = LineAmount      (per line)
    ///   Cr line.PayableTaxAccountId = TaxAmount      (per tax-bearing line)
    ///
    /// No ControlRole / PartyId on any fragment — there's no AR open
    /// item to control. The cash-side debit just lands directly on the
    /// chosen asset account.
    /// </summary>
    private static List<PostingFragment> BuildSalesReceiptFragments(
        SalesReceiptDocument salesReceipt,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();

        var depositDebitBase = Math.Round(salesReceipt.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            salesReceipt.DepositToAccountId,
            salesReceipt.TransactionCurrencyCode,
            salesReceipt.TotalAmount,
            0m,
            depositDebitBase,
            0m,
            $"Deposit for sales receipt {salesReceipt.DisplayNumber.Value}",
            PostingRole: "cash:deposit_to"));

        foreach (var line in salesReceipt.ReceiptLines)
        {
            var baseRevenue = Math.Round(line.LineAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
            fragments.Add(new PostingFragment(
                line.RevenueAccountId,
                salesReceipt.TransactionCurrencyCode,
                0m,
                line.LineAmount,
                0m,
                baseRevenue,
                BuildSourceLineDescription("Sales receipt revenue", salesReceipt.DisplayNumber.Value, line.LineNumber, line.Description),
                PostingRole: "source_line:revenue",
                SourceLineNumber: line.LineNumber));

            if (line.TaxAmount > 0m)
            {
                var baseTax = Math.Round(line.TaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                fragments.Add(new PostingFragment(
                    line.PayableTaxAccountId!.Value,
                    salesReceipt.TransactionCurrencyCode,
                    0m,
                    line.TaxAmount,
                    0m,
                    baseTax,
                    $"Sales tax for sales receipt {salesReceipt.DisplayNumber.Value} line {line.LineNumber}",
                    TaxComponentType: "sales_tax_payable",
                    PostingRole: "tax:sales_tax_payable",
                    SourceLineNumber: line.LineNumber));
            }
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// Polarity flip of <see cref="BuildSalesReceiptFragments"/>. Cash
    /// flows OUT, so the refund-from account is credited and the line
    /// revenue + tax-payable rows are debited (reversing the original
    /// sale's GL movement). No ControlRole / PartyId — same as the
    /// sales-receipt side, no AR open item to control.
    ///
    ///   Cr RefundFromAccountId      = TotalAmount
    ///   Dr line.RevenueAccountId    = LineAmount         (per line)
    ///   Dr line.PayableTaxAccountId = TaxAmount          (per tax-bearing line)
    /// </summary>
    private static List<PostingFragment> BuildRefundReceiptFragments(
        RefundReceiptDocument refundReceipt,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();

        var refundCreditBase = Math.Round(refundReceipt.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            refundReceipt.RefundFromAccountId,
            refundReceipt.TransactionCurrencyCode,
            0m,
            refundReceipt.TotalAmount,
            0m,
            refundCreditBase,
            $"Refund disbursed for refund receipt {refundReceipt.DisplayNumber.Value}",
            PostingRole: "cash:refund_from"));

        foreach (var line in refundReceipt.ReceiptLines)
        {
            var baseRevenue = Math.Round(line.LineAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
            fragments.Add(new PostingFragment(
                line.RevenueAccountId,
                refundReceipt.TransactionCurrencyCode,
                line.LineAmount,
                0m,
                baseRevenue,
                0m,
                BuildSourceLineDescription("Refund receipt revenue reversal", refundReceipt.DisplayNumber.Value, line.LineNumber, line.Description),
                PostingRole: "source_line:revenue_reversal",
                SourceLineNumber: line.LineNumber));

            if (line.TaxAmount > 0m)
            {
                var baseTax = Math.Round(line.TaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                fragments.Add(new PostingFragment(
                    line.PayableTaxAccountId!.Value,
                    refundReceipt.TransactionCurrencyCode,
                    line.TaxAmount,
                    0m,
                    baseTax,
                    0m,
                    $"Sales tax reversal for refund receipt {refundReceipt.DisplayNumber.Value} line {line.LineNumber}",
                    TaxComponentType: "sales_tax_payable",
                    PostingRole: "tax:sales_tax_payable_reversal",
                    SourceLineNumber: line.LineNumber));
            }
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// Two-fragment journal for an internal asset transfer.
    ///
    /// Same-currency:
    ///   Cr FromAccountId  = Amount
    ///   Dr ToAccountId    = Amount
    /// Cross-currency:
    ///   Cr FromAccountId  = Amount        (in from-currency)
    ///   Dr ToAccountId    = Amount * Rate (in to-currency)
    /// In both cases the base-currency amounts come from the engine's
    /// FxResolutionResult so the audit trail uses snapshot rates, not
    /// the operator's per-document rate.
    /// </summary>
    private static List<PostingFragment> BuildBankTransferFragments(
        BankTransferDocument bankTransfer,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();
        var sameCurrency = bankTransfer.FromCurrencyCode == bankTransfer.ToCurrencyCode;

        // From-side credit (cash leaves)
        var fromBase = Math.Round(bankTransfer.Amount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            bankTransfer.FromAccountId,
            bankTransfer.FromCurrencyCode,
            0m,
            bankTransfer.Amount,
            0m,
            fromBase,
            $"Transfer out {bankTransfer.DisplayNumber.Value}",
            PostingRole: "transfer:from"));

        // To-side debit (cash arrives). Same-currency = same amount;
        // cross-currency = amount * rate (operator-supplied).
        var toAmount = sameCurrency
            ? bankTransfer.Amount
            : Math.Round(bankTransfer.Amount * (bankTransfer.FxRate ?? 1m), 6, MidpointRounding.ToEven);
        var toBase = sameCurrency
            ? fromBase
            : Math.Round(toAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            bankTransfer.ToAccountId,
            bankTransfer.ToCurrencyCode,
            toAmount,
            0m,
            toBase,
            0m,
            $"Transfer in {bankTransfer.DisplayNumber.Value}",
            PostingRole: "transfer:to"));

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// Two-fragment journal: the bank account receives the total,
    /// Undeposited Funds is debited the same total. The per-item
    /// detail (sales-receipt / receive-payment refs) lives on
    /// <c>bank_deposit_items</c> for audit traceability but doesn't
    /// produce additional GL fragments — the items are already
    /// individually posted; this is just the holding-account
    /// clearance.
    ///
    ///   Dr DepositToAccountId        = TotalAmount
    ///   Cr UndepositedFundsAccountId = TotalAmount
    /// </summary>
    private static List<PostingFragment> BuildBankDepositFragments(
        BankDepositDocument bankDeposit,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();
        var totalBase = Math.Round(bankDeposit.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);

        fragments.Add(new PostingFragment(
            bankDeposit.DepositToAccountId,
            bankDeposit.TransactionCurrencyCode,
            bankDeposit.TotalAmount,
            0m,
            totalBase,
            0m,
            $"Bank deposit {bankDeposit.DisplayNumber.Value} into bank",
            PostingRole: "deposit:to_bank"));

        fragments.Add(new PostingFragment(
            bankDeposit.UndepositedFundsAccountId,
            bankDeposit.TransactionCurrencyCode,
            0m,
            bankDeposit.TotalAmount,
            0m,
            totalBase,
            $"Bank deposit {bankDeposit.DisplayNumber.Value} clears Undeposited Funds",
            PostingRole: "deposit:from_holding"));

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// Tax return GL contract — clears the period's accruals and
    /// lands the net on a filing-side row that becomes a Pay Bills
    /// (owe) or Receive Payment (refund) target downstream.
    ///
    ///   Net = Collected - InputCredits + Adjustments
    ///
    ///   Always:
    ///     Dr TaxPayableAccountId       = Collected   (clear collected accrual)
    ///     Cr TaxReceivableAccountId    = InputCredits (clear ITC accrual)
    ///   Adjustments (signed):
    ///     If > 0:   Dr TaxAdjustmentsAccountId = Adjustments
    ///     If < 0:   Cr TaxAdjustmentsAccountId = |Adjustments|
    ///   Settlement row:
    ///     Net > 0:  Cr TaxFilingLiabilityAccountId  = Net
    ///     Net < 0:  Dr TaxFilingReceivableAccountId = |Net|
    ///     Net = 0:  no settlement row (period still locks via
    ///                JournalEntryWriter status flip)
    /// </summary>
    private static List<PostingFragment> BuildTaxReturnFragments(
        TaxReturnDocument taxReturn,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();

        // 1. Clear the collected-tax accrual (Dr Tax Payable for full
        //    collected amount). Only emit when collected > 0.
        if (taxReturn.CollectedAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                taxReturn.TaxPayableAccountId,
                taxReturn.BaseCurrencyCode,
                taxReturn.CollectedAmount,
                0m,
                taxReturn.CollectedAmount,
                0m,
                $"Tax return {taxReturn.DisplayNumber.Value}: clear output-tax accrual",
                TaxComponentType: "tax_filing:collected_clear",
                PostingRole: "tax_filing:collected_clear"));
        }

        // 2. Clear the ITC accrual (Cr Tax Receivable for full ITC
        //    amount). Only emit when ITCs > 0.
        if (taxReturn.InputCreditsAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                taxReturn.TaxReceivableAccountId,
                taxReturn.BaseCurrencyCode,
                0m,
                taxReturn.InputCreditsAmount,
                0m,
                taxReturn.InputCreditsAmount,
                $"Tax return {taxReturn.DisplayNumber.Value}: clear ITC accrual",
                TaxComponentType: "tax_filing:itc_clear",
                PostingRole: "tax_filing:itc_clear"));
        }

        // 3. Adjustments — signed. Positive adjustment (operator owes
        //    more) lands on the Dr side of the adjustments account;
        //    negative adjustment (regulator owes more) lands on Cr.
        if (taxReturn.AdjustmentsAmount != 0m)
        {
            var absAdj = Math.Abs(taxReturn.AdjustmentsAmount);
            fragments.Add(taxReturn.AdjustmentsAmount > 0m
                ? new PostingFragment(
                    taxReturn.TaxAdjustmentsAccountId,
                    taxReturn.BaseCurrencyCode,
                    absAdj,
                    0m,
                    absAdj,
                    0m,
                    $"Tax return {taxReturn.DisplayNumber.Value}: adjustment (regulator owed)",
                    TaxComponentType: "tax_filing:adjustment",
                    PostingRole: "tax_filing:adjustment_dr")
                : new PostingFragment(
                    taxReturn.TaxAdjustmentsAccountId,
                    taxReturn.BaseCurrencyCode,
                    0m,
                    absAdj,
                    0m,
                    absAdj,
                    $"Tax return {taxReturn.DisplayNumber.Value}: adjustment (refund expected)",
                    TaxComponentType: "tax_filing:adjustment",
                    PostingRole: "tax_filing:adjustment_cr"));
        }

        // 4. Settlement row.
        if (taxReturn.NetAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                taxReturn.TaxFilingLiabilityAccountId,
                taxReturn.BaseCurrencyCode,
                0m,
                taxReturn.NetAmount,
                0m,
                taxReturn.NetAmount,
                $"Tax return {taxReturn.DisplayNumber.Value}: net payable to regulator",
                TaxComponentType: "tax_filing:net_payable",
                PostingRole: "tax_filing:net_payable"));
        }
        else if (taxReturn.NetAmount < 0m)
        {
            fragments.Add(new PostingFragment(
                taxReturn.TaxFilingReceivableAccountId,
                taxReturn.BaseCurrencyCode,
                Math.Abs(taxReturn.NetAmount),
                0m,
                Math.Abs(taxReturn.NetAmount),
                0m,
                $"Tax return {taxReturn.DisplayNumber.Value}: net refund expected from regulator",
                TaxComponentType: "tax_filing:net_refund",
                PostingRole: "tax_filing:net_refund"));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildCreditNoteFragments(
        CreditNoteDocument creditNote,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();

        foreach (var line in creditNote.CreditNoteLines)
        {
            var baseRevenue = Math.Round(line.LineAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
            fragments.Add(new PostingFragment(
                line.RevenueAccountId,
                creditNote.TransactionCurrencyCode,
                line.LineAmount,
                0m,
                baseRevenue,
                0m,
                BuildSourceLineDescription("Credit note revenue reversal", creditNote.DisplayNumber.Value, line.LineNumber, line.Description),
                PostingRole: "source_line:revenue_reversal",
                SourceLineNumber: line.LineNumber));

            if (line.TaxAmount > 0m)
            {
                var baseTax = Math.Round(line.TaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                fragments.Add(new PostingFragment(
                    line.PayableTaxAccountId!.Value,
                    creditNote.TransactionCurrencyCode,
                    line.TaxAmount,
                    0m,
                    baseTax,
                    0m,
                    $"Sales tax reversal for credit note {creditNote.DisplayNumber.Value} line {line.LineNumber}",
                    TaxComponentType: "sales_tax_payable",
                    PostingRole: "tax:sales_tax_payable",
                    SourceLineNumber: line.LineNumber));
            }
        }

        var arCreditBase = Math.Round(creditNote.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            creditNote.ReceivableAccountId,
            creditNote.TransactionCurrencyCode,
            0m,
            creditNote.TotalAmount,
            0m,
            arCreditBase,
            $"Accounts Receivable credit for credit note {creditNote.DisplayNumber.Value}",
            ControlRole: "accounts_receivable",
            PartyId: creditNote.PartyId,
            PostingRole: "control:accounts_receivable"));

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildBillFragments(
        BillDocument bill,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>();

        foreach (var line in bill.BillLines)
        {
            var recoverableTaxAmount = line.RecoverableTaxAmount;
            var nonRecoverableTaxAmount = line.TaxAmount - recoverableTaxAmount;
            var expenseAmount = line.LineAmount + nonRecoverableTaxAmount;

            var baseExpense = Math.Round(expenseAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
            fragments.Add(new PostingFragment(
                line.ExpenseAccountId,
                bill.TransactionCurrencyCode,
                expenseAmount,
                0m,
                baseExpense,
                0m,
                BuildSourceLineDescription("Bill expense", bill.DisplayNumber.Value, line.LineNumber, line.Description),
                PostingRole: "source_line:expense",
                SourceLineNumber: line.LineNumber));

            if (recoverableTaxAmount > 0m)
            {
                var baseTax = Math.Round(recoverableTaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                // Recoverable input tax remains a debit even if the chosen tax account is a liability
                // account shared with output tax payable; sharing the account only nets the balance.
                fragments.Add(new PostingFragment(
                    line.RecoverableTaxAccountId!.Value,
                    bill.TransactionCurrencyCode,
                    recoverableTaxAmount,
                    0m,
                    baseTax,
                    0m,
                    $"Recoverable purchase tax for bill {bill.DisplayNumber.Value} line {line.LineNumber}",
                    TaxComponentType: "purchase_tax_recoverable",
                    PostingRole: "tax:purchase_tax_recoverable",
                    SourceLineNumber: line.LineNumber));
            }
        }

        var apCreditBase = Math.Round(bill.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
        fragments.Add(new PostingFragment(
            bill.PayableAccountId,
            bill.TransactionCurrencyCode,
            0m,
            bill.TotalAmount,
            0m,
            apCreditBase,
            $"Accounts Payable for bill {bill.DisplayNumber.Value}",
            ControlRole: "accounts_payable",
            PartyId: bill.PartyId,
            PostingRole: "control:accounts_payable"));

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildVendorCreditFragments(
        VendorCreditDocument vendorCredit,
        FxResolutionResult fxResult)
    {
        var fragments = new List<PostingFragment>
        {
            new(
                vendorCredit.PayableAccountId,
                vendorCredit.TransactionCurrencyCode,
                vendorCredit.TotalAmount,
                0m,
                Math.Round(vendorCredit.TotalAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven),
                0m,
                $"Accounts Payable credit reduction for vendor credit {vendorCredit.DisplayNumber.Value}",
                ControlRole: "accounts_payable",
                PartyId: vendorCredit.PartyId,
                PostingRole: "control:accounts_payable")
        };

        foreach (var line in vendorCredit.VendorCreditLines)
        {
            var expenseAmount = line.IsTaxRecoverable
                ? line.LineAmount
                : line.LineAmount + line.TaxAmount;

            var baseExpense = Math.Round(expenseAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
            fragments.Add(new PostingFragment(
                line.ExpenseAccountId,
                vendorCredit.TransactionCurrencyCode,
                0m,
                expenseAmount,
                0m,
                baseExpense,
                BuildSourceLineDescription("Vendor credit expense reversal", vendorCredit.DisplayNumber.Value, line.LineNumber, line.Description),
                PostingRole: "source_line:expense_reversal",
                SourceLineNumber: line.LineNumber));

            if (line.TaxAmount > 0m && line.IsTaxRecoverable)
            {
                var baseTax = Math.Round(line.TaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                fragments.Add(new PostingFragment(
                    line.RecoverableTaxAccountId!.Value,
                    vendorCredit.TransactionCurrencyCode,
                    0m,
                    line.TaxAmount,
                    0m,
                    baseTax,
                    $"Recoverable purchase tax reversal for vendor credit {vendorCredit.DisplayNumber.Value} line {line.LineNumber}",
                    TaxComponentType: "purchase_tax_recoverable",
                    PostingRole: "tax:purchase_tax_recoverable",
                    SourceLineNumber: line.LineNumber));
            }
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildCreditApplicationFragments(
        CreditApplicationDocument creditApplication)
    {
        var fragments = new List<PostingFragment>();

        foreach (var line in creditApplication.ApplicationLines)
        {
            fragments.Add(new PostingFragment(
                creditApplication.ReceivableAccountId,
                creditApplication.TransactionCurrencyCode,
                line.AppliedAmount,
                0m,
                line.SourceCarryingAmountBase,
                0m,
                $"Credit application source settlement line {line.LineNumber}",
                ControlRole: "accounts_receivable",
                PartyId: creditApplication.PartyId,
                PostingRole: "settlement:credit_application_source",
                SourceLineNumber: line.LineNumber));

            fragments.Add(new PostingFragment(
                creditApplication.ReceivableAccountId,
                creditApplication.TransactionCurrencyCode,
                0m,
                line.AppliedAmount,
                0m,
                line.TargetCarryingAmountBase,
                $"Credit application target settlement line {line.LineNumber}",
                ControlRole: "accounts_receivable",
                PartyId: creditApplication.PartyId,
                PostingRole: "settlement:credit_application_target",
                SourceLineNumber: line.LineNumber));

            AppendCreditApplicationRealizedFxFragment(creditApplication, line, fragments);
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildReceivePaymentFragments(
        ReceivePaymentDocument receivePayment,
        FxResolutionResult fxResult)
    {
        var settlementBaseTotal = SettlementAmountMath.RoundBase(
            receivePayment.PaymentLines.Sum(static line => line.AppliedAmountBase));
        var carryingBaseTotal = SettlementAmountMath.RoundBase(
            receivePayment.PaymentLines.Sum(static line => line.CarryingAmountBase));
        var appliedTxTotal = receivePayment.PaymentLines.Sum(static line => line.AppliedAmount);
        var bankBaseTotal = SettlementAmountMath.RoundBase(
            settlementBaseTotal + receivePayment.ExtraDepositAmountBase);
        var fragments = new List<PostingFragment>
        {
            new(
                receivePayment.BankAccountId,
                receivePayment.TransactionCurrencyCode,
                receivePayment.TotalAmount,
                0m,
                bankBaseTotal,
                0m,
                $"Bank receipt for payment {receivePayment.DisplayNumber.Value}",
                PostingRole: "cash:receipt"),
            new(
                receivePayment.ReceivableAccountId,
                receivePayment.TransactionCurrencyCode,
                0m,
                appliedTxTotal,
                0m,
                carryingBaseTotal,
                $"Accounts Receivable settlement for payment {receivePayment.DisplayNumber.Value}",
                ControlRole: "accounts_receivable",
                PartyId: receivePayment.PartyId,
                PostingRole: "control:accounts_receivable")
        };

        if (receivePayment.ExtraDepositAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                receivePayment.CustomerDepositAccountId ?? throw new InvalidOperationException(
                    "Receive payment is missing a Customer Deposit account."),
                receivePayment.TransactionCurrencyCode,
                0m,
                receivePayment.ExtraDepositAmount,
                0m,
                receivePayment.ExtraDepositAmountBase,
                $"Customer deposit liability from overpayment {receivePayment.DisplayNumber.Value}",
                ControlRole: "customer_deposit",
                PartyId: receivePayment.PartyId,
                PostingRole: "control:customer_deposit"));
        }

        AppendReceivePaymentRealizedFxFragment(receivePayment, fragments, settlementBaseTotal, carryingBaseTotal);
        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildVendorCreditApplicationFragments(
        VendorCreditApplicationDocument vendorCreditApplication)
    {
        var fragments = new List<PostingFragment>();

        foreach (var line in vendorCreditApplication.ApplicationLines)
        {
            fragments.Add(new PostingFragment(
                vendorCreditApplication.PayableAccountId,
                vendorCreditApplication.TransactionCurrencyCode,
                line.AppliedAmount,
                0m,
                line.TargetCarryingAmountBase,
                0m,
                $"Vendor credit application target settlement line {line.LineNumber}",
                ControlRole: "accounts_payable",
                PartyId: vendorCreditApplication.PartyId,
                PostingRole: "settlement:vendor_credit_application_target",
                SourceLineNumber: line.LineNumber));

            fragments.Add(new PostingFragment(
                vendorCreditApplication.PayableAccountId,
                vendorCreditApplication.TransactionCurrencyCode,
                0m,
                line.AppliedAmount,
                0m,
                line.SourceCarryingAmountBase,
                $"Vendor credit application source settlement line {line.LineNumber}",
                ControlRole: "accounts_payable",
                PartyId: vendorCreditApplication.PartyId,
                PostingRole: "settlement:vendor_credit_application_source",
                SourceLineNumber: line.LineNumber));

            AppendVendorCreditApplicationRealizedFxFragment(vendorCreditApplication, line, fragments);
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildPayBillFragments(
        PayBillDocument payBill,
        FxResolutionResult fxResult)
    {
        var settlementBaseTotal = SettlementAmountMath.RoundBase(
            payBill.PaymentLines.Sum(static line => line.AppliedAmountBase));
        var carryingBaseTotal = SettlementAmountMath.RoundBase(
            payBill.PaymentLines.Sum(static line => line.CarryingAmountBase));
        var fragments = new List<PostingFragment>
        {
            new(
                payBill.PayableAccountId,
                payBill.TransactionCurrencyCode,
                payBill.TotalAmount,
                0m,
                carryingBaseTotal,
                0m,
                $"Accounts Payable settlement for payment {payBill.DisplayNumber.Value}",
                ControlRole: "accounts_payable",
                PartyId: payBill.PartyId,
                PostingRole: "control:accounts_payable"),
            new(
                payBill.BankAccountId,
                payBill.TransactionCurrencyCode,
                0m,
                payBill.TotalAmount,
                0m,
                settlementBaseTotal,
                $"Bank disbursement for payment {payBill.DisplayNumber.Value}",
                PostingRole: "cash:disbursement")
        };

        AppendPayBillRealizedFxFragment(payBill, fragments, settlementBaseTotal, carryingBaseTotal);
        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildFxRevaluationFragments(
        FxRevaluationDocument fxRevaluation)
    {
        var fragments = new List<PostingFragment>();

        foreach (var line in fxRevaluation.RevaluationLines)
        {
            AppendBalanceSideAwareRevaluationFragments(fxRevaluation, line, fragments);
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildOpenItemAdjustmentFragments(
        OpenItemAdjustmentDocument adjustment)
    {
        var fragments = new List<PostingFragment>();

        foreach (var line in adjustment.AdjustmentLines)
        {
            if (line.ReducesDebitBalance)
            {
                fragments.Add(new PostingFragment(
                    line.OffsetAccountId,
                    adjustment.TransactionCurrencyCode,
                    line.AdjustmentAmountTx,
                    0m,
                    line.AdjustmentAmountBase,
                    0m,
                    $"Offset for {adjustment.DisplayNumber.Value} line {line.LineNumber}",
                    PostingRole: "adjustment:offset",
                    SourceLineNumber: line.LineNumber));
                fragments.Add(new PostingFragment(
                    line.ControlAccountId,
                    adjustment.TransactionCurrencyCode,
                    0m,
                    line.AdjustmentAmountTx,
                    0m,
                    line.AdjustmentAmountBase,
                    line.Description,
                    ControlRole: line.ControlRole,
                    PartyId: line.PartyId,
                    PostingRole: $"control:{line.ControlRole}",
                    SourceLineNumber: line.LineNumber));
            }
            else
            {
                fragments.Add(new PostingFragment(
                    line.ControlAccountId,
                    adjustment.TransactionCurrencyCode,
                    line.AdjustmentAmountTx,
                    0m,
                    line.AdjustmentAmountBase,
                    0m,
                    line.Description,
                    ControlRole: line.ControlRole,
                    PartyId: line.PartyId,
                    PostingRole: $"control:{line.ControlRole}",
                    SourceLineNumber: line.LineNumber));
                fragments.Add(new PostingFragment(
                    line.OffsetAccountId,
                    adjustment.TransactionCurrencyCode,
                    0m,
                    line.AdjustmentAmountTx,
                    0m,
                    line.AdjustmentAmountBase,
                    $"Offset for {adjustment.DisplayNumber.Value} line {line.LineNumber}",
                    PostingRole: "adjustment:offset",
                    SourceLineNumber: line.LineNumber));
            }
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildReceiptGrIrPostingFragments(
        ReceiptGrIrPostingDocument document)
    {
        var fragments = new List<PostingFragment>();

        foreach (var accountGroup in document.GrIrLines.GroupBy(static line => line.InventoryAssetAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                amount,
                0m,
                amount,
                0m,
                $"Inventory asset recognition for {document.DisplayNumber.Value}",
                ControlRole: "inventory_asset",
                PostingRole: "inventory:asset_recognition"));
        }

        var creditAmount = Round6(document.GrIrLines.Sum(static line => line.AmountBase));
        fragments.Add(new PostingFragment(
            document.GrIrClearingAccountId,
            document.BaseCurrencyCode,
            0m,
            creditAmount,
            0m,
            creditAmount,
            $"GR/IR clearing for receipt {document.ReceiptDocumentId}",
            ControlRole: "grir_clearing",
            PostingRole: "control:grir_clearing"));

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// M3 Sales Issue → COGS bridge. Per item: Dr COGS / Cr Inventory
    /// Asset at the layer-frozen base cost the inventory engine already
    /// rolled up. No FX step — cost layers are stored in base only by
    /// the receipt path (per the dual-truth invariant).
    /// Multiple items grouped by their account so the resulting JE has
    /// at most one fragment per (account, side).
    /// </summary>
    private static List<PostingFragment> BuildSalesIssueCogsPostingFragments(
        SalesIssueCogsPostingDocument document)
    {
        var fragments = new List<PostingFragment>();
        var displayNumber = document.DisplayNumber.Value;

        if (document.IsReverse)
        {
            // P0-2 (C2): compensating JE for an invoice-reverse. The
            // forward sales-issue COGS posting was Dr COGS / Cr Inventory
            // Asset. The reverse swaps the legs at identical per-account
            // amounts so the GL Inventory Asset balance and the inventory
            // subledger reconcile after every invoice reverse.

            // Debit Inventory Asset (restore).
            foreach (var accountGroup in document.CogsLines.GroupBy(static line => line.InventoryAssetAccountId))
            {
                var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
                if (amount <= 0m)
                {
                    continue;
                }

                fragments.Add(new PostingFragment(
                    accountGroup.Key,
                    document.BaseCurrencyCode,
                    amount,
                    0m,
                    amount,
                    0m,
                    $"Inventory restored by sales-issue reverse {displayNumber}",
                    ControlRole: "inventory_asset",
                    PostingRole: "inventory:asset_restoration"));
            }

            // Credit COGS (un-recognise).
            foreach (var accountGroup in document.CogsLines.GroupBy(static line => line.CogsAccountId))
            {
                var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
                if (amount <= 0m)
                {
                    continue;
                }

                fragments.Add(new PostingFragment(
                    accountGroup.Key,
                    document.BaseCurrencyCode,
                    0m,
                    amount,
                    0m,
                    amount,
                    $"COGS reversed for sales-issue {displayNumber}",
                    ControlRole: "cost_of_goods_sold",
                    PostingRole: "inventory:cogs_reversal"));
            }

            EnsureBalancedBaseCurrency(fragments);
            return fragments;
        }

        // Debit COGS — group by item-resolved COGS account so a JE with
        // 50 line items posting to the same COGS account collapses to one
        // fragment.
        foreach (var accountGroup in document.CogsLines.GroupBy(static line => line.CogsAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                amount,
                0m,
                amount,
                0m,
                $"COGS for sales-issue {displayNumber}",
                ControlRole: "cost_of_goods_sold",
                PostingRole: "inventory:cogs_recognition"));
        }

        // Credit Inventory Asset — same grouping shape.
        foreach (var accountGroup in document.CogsLines.GroupBy(static line => line.InventoryAssetAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                0m,
                amount,
                0m,
                amount,
                $"Inventory consumed by sales-issue {displayNumber}",
                ControlRole: "inventory_asset",
                PostingRole: "inventory:asset_consumption"));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// M6 iter 3: per-item Dr COGS / Cr Drop-ship Clearing fragments for
    /// drop-ship lines on a posted invoice. Mirror of
    /// <see cref="BuildSalesIssueCogsPostingFragments"/> — same group-by-
    /// account collapse, same base-currency-only shape — but the credit
    /// leg targets Drop-ship Clearing (cleared by the matching vendor
    /// bill from M6 iter 2) instead of Inventory Asset.
    /// </summary>
    private static List<PostingFragment> BuildInvoiceDropShipCogsPostingFragments(
        InvoiceDropShipCogsPostingDocument document)
    {
        var fragments = new List<PostingFragment>();

        foreach (var accountGroup in document.CogsLines.GroupBy(static line => line.CogsAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                amount,
                0m,
                amount,
                0m,
                $"Drop-ship COGS for invoice {document.DisplayNumber.Value}",
                ControlRole: "cost_of_goods_sold",
                PostingRole: "drop_ship:cogs_recognition"));
        }

        foreach (var accountGroup in document.CogsLines.GroupBy(static line => line.DropShipClearingAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.AmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                0m,
                amount,
                0m,
                amount,
                $"Drop-ship clearing settlement for invoice {document.DisplayNumber.Value}",
                ControlRole: "drop_ship_clearing",
                PostingRole: "drop_ship:clearing_settlement"));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// M6 iter 4: two-leg variance write-off. The amount sign decides
    /// which side hits the clearing account:
    ///   net &gt; 0 → bill total exceeds invoice COGS → Cr Clearing / Dr PPV
    ///   net &lt; 0 → invoice COGS exceeds bill total → Dr Clearing / Cr PPV
    /// Either way the clearing returns to zero for the item; the PPV
    /// account absorbs the difference (favourable variance reads as a
    /// credit to PPV i.e. a recovery; unfavourable reads as a debit i.e.
    /// an extra cost).
    /// </summary>
    private static List<PostingFragment> BuildDropShipClearingWriteOffFragments(
        DropShipClearingWriteOffDocument document)
    {
        var fragments = new List<PostingFragment>();
        var amount = Math.Abs(Round6(document.NetClearingAmountBase));
        if (amount == 0m)
        {
            return fragments;
        }

        var memoSuffix = string.IsNullOrWhiteSpace(document.Memo) ? string.Empty : $" — {document.Memo}";

        if (document.NetClearingAmountBase > 0m)
        {
            // Over-billed: clearing carries a debit residual (vendor side
            // exceeded customer side). Reverse the clearing with a Cr,
            // and book the variance as an unfavourable Dr to PPV.
            fragments.Add(new PostingFragment(
                document.VarianceAccountId,
                document.BaseCurrencyCode,
                amount,
                0m,
                amount,
                0m,
                $"Drop-ship variance write-off for {document.ItemCode}{memoSuffix}",
                ControlRole: "purchase_price_variance",
                PostingRole: "drop_ship:writeoff_unfavourable"));
            fragments.Add(new PostingFragment(
                document.DropShipClearingAccountId,
                document.BaseCurrencyCode,
                0m,
                amount,
                0m,
                amount,
                $"Clear drop-ship residual for {document.ItemCode}{memoSuffix}",
                ControlRole: "drop_ship_clearing",
                PostingRole: "drop_ship:clearing_writeoff"));
        }
        else
        {
            // Under-billed: clearing carries a credit residual (customer
            // side exceeded vendor side — invoice posted before bill, or
            // bill arrived under expected cost). Reverse the clearing
            // with a Dr, book the variance as a favourable Cr to PPV.
            fragments.Add(new PostingFragment(
                document.DropShipClearingAccountId,
                document.BaseCurrencyCode,
                amount,
                0m,
                amount,
                0m,
                $"Clear drop-ship residual for {document.ItemCode}{memoSuffix}",
                ControlRole: "drop_ship_clearing",
                PostingRole: "drop_ship:clearing_writeoff"));
            fragments.Add(new PostingFragment(
                document.VarianceAccountId,
                document.BaseCurrencyCode,
                0m,
                amount,
                0m,
                amount,
                $"Drop-ship variance recovery for {document.ItemCode}{memoSuffix}",
                ControlRole: "purchase_price_variance",
                PostingRole: "drop_ship:writeoff_favourable"));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// M5 iter 4: per-line Dr Customer Deposit / Cr AR fragment for each
    /// applied deposit slice. Total per fragment is base-only (V1
    /// same-currency assumption inherited from M5 iter 3).
    /// </summary>
    private static List<PostingFragment> BuildCustomerDepositApplicationFragments(
        CustomerDepositApplicationDocument document)
    {
        var fragments = new List<PostingFragment>(document.ApplicationLines.Count * 2);
        foreach (var line in document.ApplicationLines)
        {
            fragments.Add(new PostingFragment(
                document.CustomerDepositAccountId,
                document.BaseCurrencyCode,
                line.AppliedAmountBase,
                0m,
                line.AppliedAmountBase,
                0m,
                $"Customer deposit applied to invoice (line {line.LineNumber})",
                ControlRole: "customer_deposit",
                PartyId: document.PartyId,
                PostingRole: "settlement:customer_deposit_clear",
                SourceLineNumber: line.LineNumber));

            fragments.Add(new PostingFragment(
                document.ReceivableAccountId,
                document.BaseCurrencyCode,
                0m,
                line.AppliedAmountBase,
                0m,
                line.AppliedAmountBase,
                $"AR reduction from customer deposit application (line {line.LineNumber})",
                ControlRole: "accounts_receivable",
                PartyId: document.PartyId,
                PostingRole: "settlement:credit_application_target",
                SourceLineNumber: line.LineNumber));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    /// <summary>
    /// M5 iter 3: Customer Deposit posting fragments.
    /// Dr Bank (DepositToAccountId) / Cr Customer Deposit (24700) at the
    /// proportional base amount derived from FxSnapshot. Single-line
    /// document — no cost layers, no AR involvement.
    /// </summary>
    private static List<PostingFragment> BuildCustomerDepositFragments(
        CustomerDepositPostingDocument deposit)
    {
        var fragments = new List<PostingFragment>(2)
        {
            new(
                deposit.DepositToAccountId,
                deposit.TransactionCurrencyCode,
                deposit.AmountTx,
                0m,
                deposit.AmountBase,
                0m,
                $"Customer deposit received {deposit.DisplayNumber.Value}",
                ControlRole: "customer_deposit_bank",
                PartyId: deposit.CustomerId,
                PostingRole: "deposit:bank_in"),
            new(
                deposit.CustomerDepositAccountId,
                deposit.TransactionCurrencyCode,
                0m,
                deposit.AmountTx,
                0m,
                deposit.AmountBase,
                $"Customer deposit liability {deposit.DisplayNumber.Value}",
                ControlRole: "customer_deposit",
                PartyId: deposit.CustomerId,
                PostingRole: "control:customer_deposit"),
        };

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    private static List<PostingFragment> BuildReceiptGrIrSettlementPostingFragments(
        ReceiptGrIrSettlementPostingDocument document)
    {
        var fragments = new List<PostingFragment>();

        // Dr GR/IR clearing for the receipt-side amount that was parked at
        // receipt time. Bringing the clearing back to zero per matched slice.
        foreach (var accountGroup in document.SettlementLines.GroupBy(static line => line.GrIrClearingAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.GrIrAmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                amount,
                0m,
                amount,
                0m,
                $"GR/IR settlement clearing for {document.DisplayNumber.Value}",
                ControlRole: "grir_clearing",
                PostingRole: "control:grir_clearing"));
        }

        // Cr the bill's expense account for the bill-side proportional amount,
        // reversing the bill's standalone Dr Expense line so the cost lives on
        // Inventory (debited by the receipt) instead of Expense.
        foreach (var accountGroup in document.SettlementLines.GroupBy(static line => line.BillOffsetAccountId))
        {
            var amount = Round6(accountGroup.Sum(static line => line.BillAmountBase));
            if (amount <= 0m)
            {
                continue;
            }

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                0m,
                amount,
                0m,
                amount,
                $"Bill-side GR/IR settlement offset for {document.DisplayNumber.Value}",
                ControlRole: "grir_bill_offset",
                PostingRole: "settlement:grir_bill_offset"));
        }

        // PPV: signed delta between bill and GR/IR. Positive = unfavorable
        // (paid more than expected) → Dr PPV. Negative = favorable → Cr PPV.
        // Lines whose bill-side amount equals their GR/IR amount contribute 0
        // and are dropped by the magnitude check below.
        foreach (var accountGroup in document.SettlementLines
            .Where(static line => line.PpvAccountId.HasValue && line.VarianceAmountBase != 0m)
            .GroupBy(static line => line.PpvAccountId!.Value))
        {
            var signedAmount = Round6(accountGroup.Sum(static line => line.VarianceAmountBase));
            if (signedAmount == 0m)
            {
                continue;
            }

            var debitBase = signedAmount > 0m ? signedAmount : 0m;
            var creditBase = signedAmount < 0m ? -signedAmount : 0m;

            fragments.Add(new PostingFragment(
                accountGroup.Key,
                document.BaseCurrencyCode,
                debitBase,
                creditBase,
                debitBase,
                creditBase,
                signedAmount > 0m
                    ? $"Purchase Price Variance (unfavorable) for {document.DisplayNumber.Value}"
                    : $"Purchase Price Variance (favorable) for {document.DisplayNumber.Value}",
                ControlRole: "purchase_price_variance",
                PostingRole: signedAmount > 0m
                    ? "ppv:unfavorable"
                    : "ppv:favorable"));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

    // H2: Realized FX gain/loss is a BASE-CURRENCY artefact — it's the
    // delta between the carrying base value of the open item (locked at
    // post-time via the invoice/bill FX snapshot) and the settlement
    // base value at payment time (locked via the payment FX snapshot).
    // There is no exposure in transaction currency, so TxDebit/TxCredit
    // are both 0 on the FX leg and only the base side carries the delta.
    //
    // The whole JE still balances on both axes:
    //   * Tx axis: bank Dr + AR Cr already balance to zero in TX
    //              currency. The FX leg adds 0/0 → still balanced.
    //   * Base axis: bank Dr (= settlementBase) vs AR Cr (= carryingBase)
    //              has a delta of (settlementBase - carryingBase). The
    //              FX leg closes that delta with the same magnitude on
    //              the opposite side, making total base Dr == total
    //              base Cr.
    //
    // Reporting note for downstream consumers (POSTING_TAX_FX_ENGINE_
    // EXECUTION_SPEC §11): aggregating ledger_entries by
    // transaction_currency_code will MISS the FX-realisation slice
    // because the FX legs contribute 0 in TX currency. Filter by
    // posting_role LIKE 'fx:realized%' on the base columns instead.
    // EnsureJournalInvariants below now requires that any fragment
    // with (TxDebit==0 && TxCredit==0 && (Debit>0 || Credit>0)) carries
    // a posting_role with the `fx:` prefix — catches future regressions
    // where a non-FX fragment accidentally drops both TX legs.
    private static void AppendReceivePaymentRealizedFxFragment(
        ReceivePaymentDocument receivePayment,
        List<PostingFragment> fragments,
        decimal settlementBaseTotal,
        decimal carryingBaseTotal)
    {
        var realizedFxAmount = SettlementAmountMath.RoundBase(settlementBaseTotal - carryingBaseTotal);
        if (realizedFxAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                receivePayment.RealizedFxGainAccountId ?? throw new InvalidOperationException(
                    "Receive payment is missing a realized FX gain account."),
                receivePayment.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                realizedFxAmount,
                $"Realized FX gain on payment {receivePayment.DisplayNumber.Value}",
                PostingRole: "fx:realized_gain"));
        }
        else if (realizedFxAmount < 0m)
        {
            fragments.Add(new PostingFragment(
                receivePayment.RealizedFxLossAccountId ?? throw new InvalidOperationException(
                    "Receive payment is missing a realized FX loss account."),
                receivePayment.TransactionCurrencyCode,
                0m,
                0m,
                Math.Abs(realizedFxAmount),
                0m,
                $"Realized FX loss on payment {receivePayment.DisplayNumber.Value}",
                PostingRole: "fx:realized_loss"));
        }
    }

    private static void AppendCreditApplicationRealizedFxFragment(
        CreditApplicationDocument creditApplication,
        CreditApplicationDocumentLine line,
        List<PostingFragment> fragments)
    {
        var realizedFxAmount = SettlementAmountMath.RoundBase(line.RealizedFxAmountBase);
        if (realizedFxAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                creditApplication.RealizedFxGainAccountId ?? throw new InvalidOperationException(
                    "Credit application is missing a realized FX gain account."),
                creditApplication.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                realizedFxAmount,
                $"Realized FX gain on credit application {creditApplication.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:realized_gain",
                SourceLineNumber: line.LineNumber));
        }
        else if (realizedFxAmount < 0m)
        {
            fragments.Add(new PostingFragment(
                creditApplication.RealizedFxLossAccountId ?? throw new InvalidOperationException(
                    "Credit application is missing a realized FX loss account."),
                creditApplication.TransactionCurrencyCode,
                0m,
                0m,
                Math.Abs(realizedFxAmount),
                0m,
                $"Realized FX loss on credit application {creditApplication.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:realized_loss",
                SourceLineNumber: line.LineNumber));
        }
    }

    private static void AppendPayBillRealizedFxFragment(
        PayBillDocument payBill,
        List<PostingFragment> fragments,
        decimal settlementBaseTotal,
        decimal carryingBaseTotal)
    {
        var realizedFxAmount = SettlementAmountMath.RoundBase(carryingBaseTotal - settlementBaseTotal);
        if (realizedFxAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                payBill.RealizedFxGainAccountId ?? throw new InvalidOperationException(
                    "Pay bill is missing a realized FX gain account."),
                payBill.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                realizedFxAmount,
                $"Realized FX gain on payment {payBill.DisplayNumber.Value}",
                PostingRole: "fx:realized_gain"));
        }
        else if (realizedFxAmount < 0m)
        {
            fragments.Add(new PostingFragment(
                payBill.RealizedFxLossAccountId ?? throw new InvalidOperationException(
                    "Pay bill is missing a realized FX loss account."),
                payBill.TransactionCurrencyCode,
                0m,
                0m,
                Math.Abs(realizedFxAmount),
                0m,
                $"Realized FX loss on payment {payBill.DisplayNumber.Value}",
                PostingRole: "fx:realized_loss"));
        }
    }

    private static void AppendVendorCreditApplicationRealizedFxFragment(
        VendorCreditApplicationDocument vendorCreditApplication,
        VendorCreditApplicationDocumentLine line,
        List<PostingFragment> fragments)
    {
        var realizedFxAmount = SettlementAmountMath.RoundBase(line.RealizedFxAmountBase);
        if (realizedFxAmount > 0m)
        {
            fragments.Add(new PostingFragment(
                vendorCreditApplication.RealizedFxLossAccountId ?? throw new InvalidOperationException(
                    "Vendor credit application is missing a realized FX loss account."),
                vendorCreditApplication.TransactionCurrencyCode,
                0m,
                0m,
                realizedFxAmount,
                0m,
                $"Realized FX loss on vendor credit application {vendorCreditApplication.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:realized_loss",
                SourceLineNumber: line.LineNumber));
        }
        else if (realizedFxAmount < 0m)
        {
            fragments.Add(new PostingFragment(
                vendorCreditApplication.RealizedFxGainAccountId ?? throw new InvalidOperationException(
                    "Vendor credit application is missing a realized FX gain account."),
                vendorCreditApplication.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                Math.Abs(realizedFxAmount),
                $"Realized FX gain on vendor credit application {vendorCreditApplication.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:realized_gain",
                SourceLineNumber: line.LineNumber));
        }
    }

    private static void AppendBalanceSideAwareRevaluationFragments(
        FxRevaluationDocument document,
        FxRevaluationDocumentLine line,
        List<PostingFragment> fragments)
    {
        var delta = SettlementAmountMath.RoundBase(line.UnrealizedAmountBase);
        if (line.IsDebitBalance)
        {
            AppendDebitBalanceRevaluationFragments(document, line, delta, fragments);
        }
        else
        {
            AppendCreditBalanceRevaluationFragments(document, line, delta, fragments);
        }
    }

    private static void AppendDebitBalanceRevaluationFragments(
        FxRevaluationDocument document,
        FxRevaluationDocumentLine line,
        decimal delta,
        List<PostingFragment> fragments)
    {
        if (delta > 0m)
        {
            fragments.Add(new PostingFragment(
                line.TargetControlAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                delta,
                0m,
                line.Description,
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                PostingRole: $"control:{line.ControlRole}",
                SourceLineNumber: line.LineNumber));
            fragments.Add(new PostingFragment(
                line.OffsetAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                delta,
                $"Unrealized FX offset for {document.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:unrealized_offset",
                SourceLineNumber: line.LineNumber));
        }
        else
        {
            var absoluteDelta = Math.Abs(delta);
            fragments.Add(new PostingFragment(
                line.OffsetAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                absoluteDelta,
                0m,
                $"Unrealized FX offset for {document.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:unrealized_offset",
                SourceLineNumber: line.LineNumber));
            fragments.Add(new PostingFragment(
                line.TargetControlAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                absoluteDelta,
                line.Description,
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                PostingRole: $"control:{line.ControlRole}",
                SourceLineNumber: line.LineNumber));
        }
    }

    private static void AppendCreditBalanceRevaluationFragments(
        FxRevaluationDocument document,
        FxRevaluationDocumentLine line,
        decimal delta,
        List<PostingFragment> fragments)
    {
        if (delta > 0m)
        {
            fragments.Add(new PostingFragment(
                line.OffsetAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                delta,
                0m,
                $"Unrealized FX offset for {document.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:unrealized_offset",
                SourceLineNumber: line.LineNumber));
            fragments.Add(new PostingFragment(
                line.TargetControlAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                delta,
                line.Description,
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                PostingRole: $"control:{line.ControlRole}",
                SourceLineNumber: line.LineNumber));
        }
        else
        {
            var absoluteDelta = Math.Abs(delta);
            fragments.Add(new PostingFragment(
                line.TargetControlAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                absoluteDelta,
                0m,
                line.Description,
                ControlRole: line.ControlRole,
                PartyId: line.PartyId,
                PostingRole: $"control:{line.ControlRole}",
                SourceLineNumber: line.LineNumber));
            fragments.Add(new PostingFragment(
                line.OffsetAccountId,
                document.TransactionCurrencyCode,
                0m,
                0m,
                0m,
                absoluteDelta,
                $"Unrealized FX offset for {document.DisplayNumber.Value} line {line.LineNumber}",
                PostingRole: "fx:unrealized_offset",
                SourceLineNumber: line.LineNumber));
        }
    }

    private static void EnsureBalancedBaseCurrency(IReadOnlyList<PostingFragment> fragments)
    {
        var debitTotal = fragments.Sum(static fragment => fragment.Debit);
        var creditTotal = fragments.Sum(static fragment => fragment.Credit);
        var delta = debitTotal - creditTotal;

        if (delta != 0m)
        {
            throw new InvalidOperationException(
                $"Posting fragments are not balanced in base currency after FX conversion. Delta: {delta:0.00####}.");
        }
    }

    private static string BuildSourceLineDescription(
        string role,
        string documentNumber,
        int lineNumber,
        string description)
    {
        var normalizedRole = string.IsNullOrWhiteSpace(role)
            ? "Source line"
            : role.Trim();
        var normalizedDocumentNumber = string.IsNullOrWhiteSpace(documentNumber)
            ? "source document"
            : documentNumber.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? "No description"
            : description.Trim();

        return $"{normalizedRole} for {normalizedDocumentNumber} line {lineNumber}: {normalizedDescription}";
    }

    private static decimal Round6(decimal value) =>
        Math.Round(value, 6, MidpointRounding.ToEven);
}

public sealed class DefaultJournalAggregator : IJournalAggregator
{
    public JournalEntryDraft Aggregate(
        IPostingDocument document,
        IReadOnlyList<PostingFragment> fragments,
        FxResolutionResult fxResult)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(fxResult);
        EnsureJournalInvariants(document, fragments, fxResult);

        var lines = fragments
            .Select((fragment, index) => new JournalEntryDraftLine(
                index + 1,
                fragment.AccountId,
                fragment.Description,
                fragment.TxDebit,
                fragment.TxCredit,
                fragment.Debit,
                fragment.Credit,
                fragment.TaxComponentType,
                fragment.ControlRole,
                fragment.PartyId,
                ResolvePostingRole(fragment),
                fragment.SourceLineNumber))
            .ToArray();

        return new JournalEntryDraft(
            document.CompanyId,
            document.SourceType,
            document.Id,
            document.TransactionCurrencyCode,
            document.BaseCurrencyCode,
            fxResult.Snapshot,
            lines);
    }

    private static string ResolvePostingRole(PostingFragment fragment)
    {
        if (!string.IsNullOrWhiteSpace(fragment.PostingRole))
        {
            return fragment.PostingRole.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fragment.ControlRole))
        {
            return $"control:{fragment.ControlRole.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(fragment.TaxComponentType))
        {
            return $"tax:{fragment.TaxComponentType.Trim()}";
        }

        return "source_line";
    }

    private static void EnsureJournalInvariants(
        IPostingDocument document,
        IReadOnlyList<PostingFragment> fragments,
        FxResolutionResult fxResult)
    {
        if (fragments.Count == 0)
        {
            throw new InvalidOperationException("Posting fragments must contain at least one journal line.");
        }

        if (fxResult.Snapshot.BaseCurrencyCode != document.BaseCurrencyCode ||
            fxResult.Snapshot.QuoteCurrencyCode != document.TransactionCurrencyCode)
        {
            throw new InvalidOperationException(
                "FX snapshot currency pair does not match the posting document currency context.");
        }

        if (document.TransactionCurrencyCode == document.BaseCurrencyCode && fxResult.Snapshot.Rate != 1m)
        {
            throw new InvalidOperationException(
                "Base-currency posting requires an identity FX rate.");
        }

        foreach (var fragment in fragments)
        {
            if (fragment.AccountId == Guid.Empty)
            {
                throw new InvalidOperationException("Posting fragment account id is required.");
            }

            if (fragment.CurrencyCode != document.TransactionCurrencyCode)
            {
                throw new InvalidOperationException(
                    "Posting fragment currency does not match the posting document transaction currency.");
            }

            if (fragment.TxDebit < 0m ||
                fragment.TxCredit < 0m ||
                fragment.Debit < 0m ||
                fragment.Credit < 0m)
            {
                throw new InvalidOperationException("Posting fragments cannot contain negative debit or credit amounts.");
            }

            if (fragment.TxDebit > 0m && fragment.TxCredit > 0m)
            {
                throw new InvalidOperationException(
                    "Posting fragment cannot carry both transaction debit and transaction credit amounts.");
            }

            if (fragment.Debit > 0m && fragment.Credit > 0m)
            {
                throw new InvalidOperationException(
                    "Posting fragment cannot carry both base debit and base credit amounts.");
            }

            // H2: invariant for the realized FX legs. They legitimately
            // carry TxDebit=TxCredit=0 because realized FX is a base-
            // currency-only concept (see comment on
            // AppendReceivePaymentRealizedFxFragment). But ANY other
            // fragment with both TX legs zero AND a non-zero base leg
            // is a bug — it would silently make TX-grouped reports
            // understate that fragment's GL impact. Reject early
            // with the role name so the regression is obvious.
            //
            // X-5: extend the bypass to UNREALIZED FX revaluation. By
            // accounting definition, period-end remeasurement only
            // adjusts the BASE carrying amount of a foreign-currency
            // AR/AP open item — the TX amount is unchanged because
            // the obligation is still owed in the foreign currency.
            // Both fragments emitted by AppendBalanceSideAwareRevaluation
            // Fragments (control:accounts_receivable / :accounts_payable
            // on one side, fx:unrealized_offset on the other) therefore
            // legitimately carry TxDebit=TxCredit=0. TX-grouped reports
            // SHOULD NOT pick these up — a revaluation isn't a TX-
            // currency transaction, just a valuation adjustment. The
            // original H2 author only allow-listed fx:realized*; this
            // bypass widens it for the FX revaluation document type
            // without weakening the rule for any other posting flow.
            var hasBaseImpact = fragment.Debit > 0m || fragment.Credit > 0m;
            var hasTxImpact = fragment.TxDebit > 0m || fragment.TxCredit > 0m;
            if (hasBaseImpact && !hasTxImpact)
            {
                var role = fragment.PostingRole ?? string.Empty;
                var isFxRevaluation = document is FxRevaluationDocument;
                if (!isFxRevaluation && !role.StartsWith(RealizedFxPostingRolePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Posting fragment carries base-currency amounts with both transaction-currency legs zero, but its posting_role '{role}' is not a realized-FX leg. Only fragments with posting_role starting with '{RealizedFxPostingRolePrefix}' may legally drop both TX legs.");
                }
            }
        }

        var transactionDelta = fragments.Sum(static fragment => fragment.TxDebit) -
            fragments.Sum(static fragment => fragment.TxCredit);
        if (transactionDelta != 0m)
        {
            throw new InvalidOperationException(
                $"Posting fragments are not balanced in transaction currency. Delta: {transactionDelta:0.00####}.");
        }

        var baseDelta = fragments.Sum(static fragment => fragment.Debit) -
            fragments.Sum(static fragment => fragment.Credit);
        if (baseDelta != 0m)
        {
            throw new InvalidOperationException(
                $"Posting fragments are not balanced in base currency. Delta: {baseDelta:0.00####}.");
        }
    }

    // H2: posting_role prefix marking a fragment as a realized FX
    // gain/loss leg — see EnsureJournalInvariants for the rationale.
    // Constants used in BuildXxxRealizedFxFragment calls:
    //   fx:realized_gain    fx:realized_loss
    // Keep these strings in sync with the literals at the
    // AppendReceivePaymentRealizedFxFragment / AppendPayBillRealizedFx
    // Fragment / AppendCreditApplicationRealizedFxFragment /
    // AppendVendorCreditApplicationRealizedFxFragment call sites.
    private const string RealizedFxPostingRolePrefix = "fx:realized";
}

public sealed class GeneratedJournalEntryWriter : IJournalEntryWriter
{
    public Task<JournalEntryWriteResult> WriteAsync(
        JournalEntryDraft draft,
        PostingContext context,
        CancellationToken cancellationToken)
    {
        var journalEntryId = Guid.NewGuid();
        var displayNumber = $"JE-{DateTime.UtcNow:yyyyMMddHHmmss}";
        return Task.FromResult(new JournalEntryWriteResult(journalEntryId, displayNumber));
    }
}
