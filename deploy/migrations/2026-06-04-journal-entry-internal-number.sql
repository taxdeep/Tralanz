-- Persistent clean internal number (J00001…) for every journal entry. JE is
-- the accounting core, so it carries this extra system-assigned number in
-- addition to its entity_number. Assigned by a BEFORE INSERT trigger so every
-- code path that writes a journal_entries row gets one — no per-writer change.
begin;

alter table journal_entries add column if not exists internal_number text;

-- Backfill existing rows: J + 5-digit sequence per company, by creation order.
update journal_entries je
set internal_number = sub.jn
from (
    select id,
           'J' || lpad((row_number() over (partition by company_id order by created_at, id))::text, 5, '0') as jn
    from journal_entries
) sub
where je.id = sub.id and je.internal_number is null;

-- Seed the per-company counter so new entries continue after the backfill.
insert into company_numbering_sequences (company_id, scope_key, prefix, next_number, padding)
    select company_id, 'journal-entry-internal', 'J', count(*) + 1, 5
    from journal_entries
    group by company_id
on conflict (company_id, scope_key)
    do update set next_number = greatest(company_numbering_sequences.next_number, excluded.next_number);

create or replace function assign_journal_entry_internal_number() returns trigger as $func$
declare
    issued bigint;
begin
    if new.internal_number is null then
        insert into company_numbering_sequences (company_id, scope_key, prefix, next_number, padding)
            values (new.company_id, 'journal-entry-internal', 'J', 1, 5)
            on conflict (company_id, scope_key) do nothing;
        update company_numbering_sequences
            set next_number = next_number + 1
            where company_id = new.company_id and scope_key = 'journal-entry-internal'
            returning next_number - 1 into issued;
        new.internal_number := 'J' || lpad(issued::text, 5, '0');
    end if;
    return new;
end;
$func$ language plpgsql;

drop trigger if exists trg_journal_entry_internal_number on journal_entries;
create trigger trg_journal_entry_internal_number
    before insert on journal_entries
    for each row execute function assign_journal_entry_internal_number();

commit;
