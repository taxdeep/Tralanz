window.citusSysAdminAuth = {
    getSessionToken: function () {
        return window.sessionStorage.getItem("citus.sysadmin.session");
    },
    setSessionToken: function (token) {
        if (!token) {
            return;
        }

        window.sessionStorage.setItem("citus.sysadmin.session", token);
    },
    clearSessionToken: function () {
        window.sessionStorage.removeItem("citus.sysadmin.session");
    }
};
