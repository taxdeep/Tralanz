// Auto-closes any open <details class="citus-popover"> (e.g.
// citus-user-menu, citus-create-menu) when the user clicks outside
// it. <details> natively toggles when the summary is clicked but
// doesn't auto-collapse on outside click — that's the only behaviour
// this script adds.
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
                popover.removeAttribute('open');
            }
        });
    });
})();
