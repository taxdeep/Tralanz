// Citus numeric input filter — strips non-numeric characters from
// inputs marked with inputmode="decimal" or inputmode="numeric".
//
// Why this exists:
//   The chromium -webkit-text-fill-color bug forced several amount
//   inputs (JE Debit/Credit, receive-payment allocations, etc.) to
//   drop type="number" in favour of type="text" + inputmode="decimal".
//   That fixed the invisible-text issue but reopened the older one:
//   the input now physically accepts letters, even though the bound
//   ParseDecimal silently coerces "abc" -> 0. The user sees their
//   keystrokes appear and assumes the value was captured. We want
//   the input to refuse non-numeric characters at the keystroke
//   level so the visual matches the bound state.
//
// How it works:
//   A single capture-phase 'input' listener on document. When a
//   target carries inputmode="decimal" (digits + minus + . + ,) or
//   inputmode="numeric" (digits + minus), we strip everything else
//   from target.value before Blazor's bubble-phase @oninput handler
//   reads it, then restore the cursor to the position-equivalent of
//   where the user was typing. Because we mutate target.value
//   in-place (no event re-dispatch), there is no recursion.
//
// Scope:
//   Opt-in via the inputmode attribute. RadzenNumeric does not set
//   inputmode, so its internal DOM is unaffected. Plain
//   <input type="number"> already rejects most non-numerics at the
//   browser level and is also unaffected.

(function () {
    'use strict';

    var DECIMAL_RE = /[^0-9.\-,]/g;
    var NUMERIC_RE = /[^0-9\-]/g;

    function regexFor(input) {
        var mode = input.getAttribute('inputmode');
        if (mode === 'decimal') return DECIMAL_RE;
        if (mode === 'numeric') return NUMERIC_RE;
        return null;
    }

    document.addEventListener('input', function (event) {
        var target = event.target;
        if (!(target instanceof HTMLInputElement)) return;
        var re = regexFor(target);
        if (!re) return;

        var original = target.value;
        var cleaned = original.replace(re, '');
        if (cleaned === original) return;

        // Cursor lands at the count of valid characters that were
        // before the original cursor position. This way typing "a"
        // between "1" and "2" leaves the caret right after "1".
        var cursor = target.selectionStart;
        if (cursor === null || cursor === undefined) {
            cursor = original.length;
        }
        var validBeforeCursor = original.slice(0, cursor).replace(re, '').length;

        target.value = cleaned;
        try {
            target.setSelectionRange(validBeforeCursor, validBeforeCursor);
        } catch (_) {
            // setSelectionRange throws on some input types (notably
            // type="number" in Firefox). Safe to ignore — value is
            // already sanitised, the visible cursor will land at the
            // end which is acceptable for the rare cases that hit.
        }
    }, true);
})();
