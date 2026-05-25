window.citusBusinessAuth = {
    getSessionToken: function () {
        return window.sessionStorage.getItem("citus.business.session");
    },
    setSessionToken: function (token) {
        if (!token) {
            return;
        }

        window.sessionStorage.setItem("citus.business.session", token);
    },
    clearSessionToken: function () {
        window.sessionStorage.removeItem("citus.business.session");
    },

    // Remember-email: persists ONLY the email field on the operator's
    // device so they don't have to retype it next visit. Password is
    // never persisted. Stored under a per-host localStorage key — clears
    // automatically when the user unchecks the box on the next sign-in.
    getRememberedEmail: function () {
        try {
            return window.localStorage.getItem("citus.business.rememberedEmail") || "";
        } catch (e) {
            return "";
        }
    },
    setRememberedEmail: function (email) {
        try {
            if (email) {
                window.localStorage.setItem("citus.business.rememberedEmail", email);
            } else {
                window.localStorage.removeItem("citus.business.rememberedEmail");
            }
        } catch (e) {
            // Quota / private-mode failure: silently drop. The form still
            // works without persistence.
        }
    }
};
