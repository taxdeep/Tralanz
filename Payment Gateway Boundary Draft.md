Payment Gateway Boundary Draft

PG.1 Module Definition

PaymentGateway is a separate payment-channel module.

It owns:

payment request / hosted payment session
payment attempt
provider transaction status
provider refund status
dispute / chargeback status
payout / fee / settlement metadata
webhook ingestion
provider idempotency and replay protection

PaymentGateway is not the AR module.

PG.2 Official Payment Gateway Scope

The Payment Gateway module may include:

PaymentRequest
HostedPaymentSession
PaymentAttempt
GatewayTransaction
GatewayRefundEvent
GatewayDisputeEvent
GatewayPayoutMetadata
PG.3 Payment Gateway Does Not Own AR Truth

Payment Gateway does not own:

invoice balance truth
AR aging truth
customer credit truth
customer receipt application truth
credit note truth
write-off truth
statement truth

Gateway status must not directly replace AR accounting truth.

PG.4 Gateway-to-AR Rule

PaymentGateway status != AR status

But:

PaymentGateway event -> may trigger AR action

Gateway may report normalized outcomes such as:

payment_confirmed
payment_partially_confirmed
refund_confirmed
dispute_opened
dispute_resolved
chargeback_confirmed

AR then decides whether to:

create customer receipt
create partial receipt
keep unapplied cash
trigger refund flow
trigger dispute / exception flow
update invoice balance through governed application logic
PG.5 Receive Payment Boundary

ReceivePayment belongs to AR.

Payment Gateway is only one supported payment method / payment source.

Other payment methods may include:

cash
cheque
bank transfer
e-transfer
manual card entry
gateway

Therefore:

AR owns CustomerReceipt
PaymentGateway only supplies one external payment path
PG.6 Refund / Dispute Governance

Provider refund or dispute events do not automatically rewrite AR truth.

Rules:

provider refund must be normalized first
AR / Posting Engine must decide accounting outcome
dispute / chargeback must enter governed exception flow
gateway-origin events must remain linked but must not directly overwrite historical receipt truth
PG.7 Formal Boundary Conclusion

PaymentGateway owns external provider payment-channel truth.
AR owns customer receivable and receipt truth.
Posting Engine owns formal accounting-entry truth.
Gateway events may influence AR, but gateway status may not replace AR accounting truth.