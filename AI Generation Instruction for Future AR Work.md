AI Generation Instruction for Future AR Work

All future AI-generated AR implementation must follow these rules:

treat AR and PaymentGateway as separate root modules
do not place gateway provider logic inside AR truth objects
do not collapse Return, CreditNote, and Refund into one generic adjustment object
do not collapse CustomerReceipt and PaymentApplication into one hidden screen-only behavior
do not let gateway status directly define invoice accounting status
all formal accounting consequences must remain source-linked and Posting-Engine-driven
AR outputs such as aging, statement, collection, and write-off must remain part of the formal AR module, not temporary report-side add-ons