-- Align the bills status constraint with the Bill draft lifecycle used by
-- PostgresBillDocumentRepository and PostBillCommandHandler.

alter table bills
  drop constraint if exists bills_status_chk;

alter table bills
  add constraint bills_status_chk check (
    status in ('draft', 'submitted', 'cancelled', 'posted', 'partially_paid', 'paid', 'voided', 'reversed')
  );
