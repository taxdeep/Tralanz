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
    }
};
