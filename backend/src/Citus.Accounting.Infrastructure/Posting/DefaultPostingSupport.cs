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
            if (appliedTotal != receivePayment.TotalAmount)
            {
                throw new InvalidOperationException("Receive payment total must equal the sum of its application lines.");
            }

            var expectedBaseTotal = SettlementAmountMath.RoundBase(
                receivePayment.TotalAmount * (receivePayment.FxSnapshot?.Rate ?? 1m));
            var appliedBaseTotal = SettlementAmountMath.RoundBase(
                receivePayment.PaymentLines.Sum(static line => line.AppliedAmountBase));
            if (appliedBaseTotal != expectedBaseTotal)
            {
                throw new InvalidOperationException(
                    "Receive payment application base amounts do not reconcile to the document settlement base total.");
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

            if (grIrSettlement.SettlementLines.Any(static line => line.AmountBase <= 0m))
            {
                throw new InvalidOperationException("Receipt GR/IR settlement posting lines must carry positive base amounts.");
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
            _ => throw new NotSupportedException(
                $"Document type '{document.SourceType}' is not yet supported by the fragment builder.")
        };
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
            var expenseAmount = line.IsTaxRecoverable
                ? line.LineAmount
                : line.LineAmount + line.TaxAmount;

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

            if (line.TaxAmount > 0m && line.IsTaxRecoverable)
            {
                var baseTax = Math.Round(line.TaxAmount * fxResult.Snapshot.Rate, 2, MidpointRounding.ToEven);
                fragments.Add(new PostingFragment(
                    line.RecoverableTaxAccountId!.Value,
                    bill.TransactionCurrencyCode,
                    line.TaxAmount,
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
        var fragments = new List<PostingFragment>
        {
            new(
                receivePayment.BankAccountId,
                receivePayment.TransactionCurrencyCode,
                receivePayment.TotalAmount,
                0m,
                settlementBaseTotal,
                0m,
                $"Bank receipt for payment {receivePayment.DisplayNumber.Value}",
                PostingRole: "cash:receipt"),
            new(
                receivePayment.ReceivableAccountId,
                receivePayment.TransactionCurrencyCode,
                0m,
                receivePayment.TotalAmount,
                0m,
                carryingBaseTotal,
                $"Accounts Receivable settlement for payment {receivePayment.DisplayNumber.Value}",
                ControlRole: "accounts_receivable",
                PartyId: receivePayment.PartyId,
                PostingRole: "control:accounts_receivable")
        };

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

    private static List<PostingFragment> BuildReceiptGrIrSettlementPostingFragments(
        ReceiptGrIrSettlementPostingDocument document)
    {
        var fragments = new List<PostingFragment>();

        foreach (var accountGroup in document.SettlementLines.GroupBy(static line => line.GrIrClearingAccountId))
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
                $"GR/IR settlement clearing for {document.DisplayNumber.Value}",
                ControlRole: "grir_clearing",
                PostingRole: "control:grir_clearing"));
        }

        foreach (var accountGroup in document.SettlementLines.GroupBy(static line => line.BillOffsetAccountId))
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
                $"Bill-side GR/IR settlement offset for {document.DisplayNumber.Value}",
                ControlRole: "grir_bill_offset",
                PostingRole: "settlement:grir_bill_offset"));
        }

        EnsureBalancedBaseCurrency(fragments);
        return fragments;
    }

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
