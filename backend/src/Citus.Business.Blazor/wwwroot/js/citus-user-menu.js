// Click-handling for any open <details class="citus-popover"> (e.g.
// citus-user-menu, citus-create-menu). Three behaviours layered on
// top of the native <details> toggle:
//
//   1. Click OUTSIDE the popover            → close
//   2. Click an <a> / [data-citus-popover-close] INSIDE → close
//      (the link's default navigation still fires; we just collapse
//       the panel as a side effect so the menu doesn't sit open
//       behind the new page)
//   3. Click any non-link element INSIDE    → keep open
//      (lets the user scroll, select text, or click column titles
//       without dismissing the menu by accident)
//
// Self-attaching: the listener is wired once per page load (Blazor's
// interactive renderer doesn't re-execute scripts on circuit reconnect)
// and uses event delegation against the document so popovers rendered
// later still get the behaviour.
(function () {
    if (window.__citusPopoverWired) {
        return;
    }
    window.__citusPopoverWired = true;

    document.addEventListener('click', function (event) {
        var openPopovers = document.querySelectorAll('details.citus-popover[open]');
        if (openPopovers.length === 0) {
            return;
        }
        openPopovers.forEach(function (popover) {
            if (!popover.contains(event.target)) {
                // Behaviour 1 — outside click.
                popover.removeAttribute('open');
                return;
            }
            // Inside click. Only collapse when the target opted in:
            // any <a> (links navigate, panel should follow) or any
            // element marked data-citus-popover-close (lets a button
            // dismiss the popover without being an actual link).
            // Plain text / icon / column-title clicks keep the menu
            // open by intention — the user might be scanning the panel.
            if (event.target.closest('a, [data-citus-popover-close]')) {
                popover.removeAttribute('open');
            }
        });
    });
})();
