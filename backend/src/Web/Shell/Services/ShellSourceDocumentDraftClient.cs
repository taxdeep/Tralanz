using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Web.Shell.Services;

public sealed class ShellSourceDocumentDraftClient(HttpClient httpClient, ILogger<ShellSourceDocumentDraftClient> logger)
{
    public Task<WebShellAuthenticatedApiResult<ShellSalesSourceDocumentDraftReadModel>> GetInvoiceDraftAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync<ShellSalesSourceDocumentDraftReadModel>(
            $"accounting/invoices/drafts/{documentId:D}?companyId={companyId:D}",
            "invoice draft",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSalesSourceDocumentDraftReadModel>> GetCreditNoteDraftAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync<ShellSalesSourceDocumentDraftReadModel>(
            $"accounting/credit-notes/drafts/{documentId:D}?companyId={companyId:D}",
            "credit note draft",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellPurchaseSourceDocumentDraftReadModel>> GetBillDraftAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync<ShellPurchaseSourceDocumentDraftReadModel>(
            $"accounting/bills/drafts/{documentId:D}?companyId={companyId:D}",
            "bill draft",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellPurchaseSourceDocumentDraftReadModel>> GetVendorCreditDraftAsync(
        Guid companyId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        GetAsync<ShellPurchaseSourceDocumentDraftReadModel>(
            $"accounting/vendor-credits/drafts/{documentId:D}?companyId={companyId:D}",
            "vendor credit draft",
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SaveInvoiceDraftAsync(
        Guid? documentId,
        ShellSalesSourceDocumentDraftSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            request.CompanyId,
            request.UserId,
            request.CustomerId,
            InvoiceDate = request.DocumentDate,
            request.DueDate,
            request.TransactionCurrencyCode,
            request.BaseCurrencyCode,
            request.FxSnapshotId,
            request.FxRate,
            request.FxEffectiveDate,
            request.FxSource,
            request.Memo,
            Lines = request.Lines.Select(static line => new
            {
                line.LineNumber,
                line.RevenueAccountId,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.TaxCodeId,
                line.TaxAmount
            }).ToArray()
        };

        return SaveAsync(documentId, "accounting/invoices/drafts", payload, cancellationToken);
    }

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SubmitInvoiceDraftAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        TransitionDraftAsync(
            $"accounting/invoices/drafts/{documentId:D}/submit",
            new
            {
                CompanyId = companyId,
                UserId = userId
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SaveCreditNoteDraftAsync(
        Guid? documentId,
        ShellSalesSourceDocumentDraftSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            request.CompanyId,
            request.UserId,
            request.CustomerId,
            CreditNoteDate = request.DocumentDate,
            request.DueDate,
            request.TransactionCurrencyCode,
            request.BaseCurrencyCode,
            request.FxSnapshotId,
            request.FxRate,
            request.FxEffectiveDate,
            request.FxSource,
            request.Memo,
            Lines = request.Lines.Select(static line => new
            {
                line.LineNumber,
                line.RevenueAccountId,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.TaxCodeId,
                line.TaxAmount
            }).ToArray()
        };

        return SaveAsync(documentId, "accounting/credit-notes/drafts", payload, cancellationToken);
    }

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SaveBillDraftAsync(
        Guid? documentId,
        ShellPurchaseSourceDocumentDraftSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            request.CompanyId,
            request.UserId,
            request.VendorId,
            BillDate = request.DocumentDate,
            request.DueDate,
            request.TransactionCurrencyCode,
            request.BaseCurrencyCode,
            request.FxSnapshotId,
            request.FxRate,
            request.FxEffectiveDate,
            request.FxSource,
            request.Memo,
            Lines = request.Lines.Select(static line => new
            {
                line.LineNumber,
                line.ExpenseAccountId,
                line.Description,
                line.LineAmount,
                line.TaxCodeId,
                line.TaxAmount,
                line.IsTaxRecoverable,
                line.ItemId,
                line.WarehouseId,
                line.UomCode,
                line.Quantity,
                line.UnitCost
            }).ToArray()
        };

        return SaveAsync(documentId, "accounting/bills/drafts", payload, cancellationToken);
    }

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SaveVendorCreditDraftAsync(
        Guid? documentId,
        ShellPurchaseSourceDocumentDraftSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            request.CompanyId,
            request.UserId,
            request.VendorId,
            VendorCreditDate = request.DocumentDate,
            request.DueDate,
            request.TransactionCurrencyCode,
            request.BaseCurrencyCode,
            request.FxSnapshotId,
            request.FxRate,
            request.FxEffectiveDate,
            request.FxSource,
            request.Memo,
            Lines = request.Lines.Select(static line => new
            {
                line.LineNumber,
                line.ExpenseAccountId,
                line.Description,
                line.LineAmount,
                line.TaxCodeId,
                line.TaxAmount,
                line.IsTaxRecoverable
            }).ToArray()
        };

        return SaveAsync(documentId, "accounting/vendor-credits/drafts", payload, cancellationToken);
    }

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>> PostInvoiceAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/invoices/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"invoice:{documentId:D}"
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>> PostCreditNoteAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/credit-notes/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"credit-note:{documentId:D}"
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>> PostBillAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/bills/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"bill:{documentId:D}"
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SubmitBillDraftAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        TransitionDraftAsync(
            $"accounting/bills/drafts/{documentId:D}/submit",
            new
            {
                CompanyId = companyId,
                UserId = userId
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> CancelSubmittedBillAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        TransitionDraftAsync(
            $"accounting/bills/drafts/{documentId:D}/cancel",
            new
            {
                CompanyId = companyId,
                UserId = userId
            },
            cancellationToken);

    public Task<WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>> PostVendorCreditAsync(
        Guid companyId,
        Guid userId,
        Guid documentId,
        Guid? acceptedFxSnapshotId,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            $"accounting/vendor-credits/{documentId:D}/post",
            new
            {
                CompanyId = companyId,
                UserId = userId,
                AcceptedFxSnapshotId = acceptedFxSnapshotId,
                IdempotencyKey = $"vendor-credit:{documentId:D}"
            },
            cancellationToken);

    private async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> SaveAsync(
        Guid? documentId,
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = documentId.HasValue
                ? await httpClient.PutAsJsonAsync($"{path}/{documentId.Value:D}", payload, cancellationToken)
                : await httpClient.PostAsJsonAsync(path, payload, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var saved = await response.Content.ReadFromJsonAsync<ShellSourceDocumentDraftSaveResult>(cancellationToken);
                return saved is null
                    ? WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure("Draft save succeeded but returned an empty payload.")
                    : WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Success(saved);
            }

            var message = await TryReadMessageAsync(response, cancellationToken);
            logger.LogWarning(
                "Source document draft save failed for {Path} with status code {StatusCode}: {Message}",
                path,
                (int)response.StatusCode,
                message);

            return response.StatusCode == HttpStatusCode.NotFound
                ? WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.NotFound(message)
                : WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to save source document draft using request path {Path}.", path);
            return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(
                "Draft save failed because the source-document request could not be completed.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>> TransitionDraftAsync(
        string requestUri,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var saved = await response.Content.ReadFromJsonAsync<ShellSourceDocumentDraftSaveResult>(cancellationToken);
                return saved is null
                    ? WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure("Draft transition succeeded but returned an empty payload.")
                    : WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Success(saved);
            }

            var message = await TryReadMessageAsync(response, cancellationToken);
            logger.LogWarning(
                "Source document draft transition failed for {RequestUri} with status code {StatusCode}: {Message}",
                requestUri,
                (int)response.StatusCode,
                message);

            return response.StatusCode == HttpStatusCode.NotFound
                ? WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.NotFound(message)
                : WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to transition source document draft using request {RequestUri}.", requestUri);
            return WebShellAuthenticatedApiResult<ShellSourceDocumentDraftSaveResult>.Failure(
                "Draft transition failed because the source-document request could not be completed.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>> PostAsync(
        string requestUri,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>.RequiresAuthentication();
            }

            if (response.IsSuccessStatusCode)
            {
                var posted = await response.Content.ReadFromJsonAsync<ShellSourceDocumentPostResult>(cancellationToken);
                return posted is null
                    ? WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>.Failure("Draft posting succeeded but returned an empty payload.")
                    : WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>.Success(posted);
            }

            var message = await TryReadMessageAsync(response, cancellationToken);
            logger.LogWarning(
                "Source document posting failed for {RequestUri} with status code {StatusCode}: {Message}",
                requestUri,
                (int)response.StatusCode,
                message);

            return response.StatusCode == HttpStatusCode.NotFound
                ? WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>.NotFound(message)
                : WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>.Failure(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to post source document using request {RequestUri}.", requestUri);
            return WebShellAuthenticatedApiResult<ShellSourceDocumentPostResult>.Failure(
                "Posting failed because the source-document request could not be completed.");
        }
    }

    private async Task<WebShellAuthenticatedApiResult<T>> GetAsync<T>(
        string requestUri,
        string documentLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return WebShellAuthenticatedApiResult<T>.RequiresAuthentication();
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return WebShellAuthenticatedApiResult<T>.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return WebShellAuthenticatedApiResult<T>.Failure(await TryReadMessageAsync(response, cancellationToken));
            }

            var draft = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return draft is null
                ? WebShellAuthenticatedApiResult<T>.Failure($"The {documentLabel} request succeeded but returned an empty payload.")
                : WebShellAuthenticatedApiResult<T>.Success(draft);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load source document draft from {RequestUri}.", requestUri);
            return WebShellAuthenticatedApiResult<T>.Failure($"Unable to load the {documentLabel} from the accounting API.");
        }
    }

    private static async Task<string> TryReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "Draft document was not found in the active company context.";
        }

        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            if (payload?["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch
        {
        }

        return $"Draft save failed with HTTP {(int)response.StatusCode}.";
    }
}
