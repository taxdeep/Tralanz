// Closes any open <details class="citus-user-menu"> when the user
// clicks anywhere outside the dropdown. <details> natively toggles
// when the summary is clicked but doesn't auto-collapse on outside
// click — this is the single line of behaviour we add via JS.
//
// Self-attaching: the listener is wired once per page load (Blazor's
// interactive renderer doesn't re-execute scripts on circuit reconnect)
// and uses event delegation against the document so dropdowns rendered
// later still get the behaviour.
(function () {
    if (window.__citusUserMenuWired) {
        return;
    }
    window.__citusUserMenuWired = true;

    document.addEventListener('click', function (event) {
        var openMenus = document.querySelectorAll('details.citus-user-menu[open]');
        if (openMenus.length === 0) {
            return;
        }
        openMenus.forEach(function (menu) {
            if (!menu.contains(event.target)) {
                menu.removeAttribute('open');
            }
        });
    });
})();
