(function () {
    const channel = "aiseworks.desktopBridge";
    const handlers = new Set();
    const pendingRequests = new Map();
    let sequence = 0;
    let lastCommand = null;

    function hasHostBridge() {
        return Boolean(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);
    }

    function markDesktopHost() {
        if (!hasHostBridge()) {
            return;
        }

        document.documentElement.classList.add("aiseworks-desktop-host");
        document.documentElement.dataset.aiseworksHost = "desktop";

        if (document.body) {
            document.body.classList.add("aiseworks-desktop-host");
        }
    }

    function nextId(prefix) {
        sequence += 1;
        return `${prefix}-${Date.now()}-${sequence}`;
    }

    function normalizeMessage(raw) {
        if (!raw) {
            return null;
        }

        if (typeof raw === "string") {
            try {
                return JSON.parse(raw);
            } catch {
                return null;
            }
        }

        return raw;
    }

    function post(type, payload, id) {
        if (!hasHostBridge()) {
            return false;
        }

        window.chrome.webview.postMessage({
            channel,
            direction: "web-to-shell",
            type,
            id: id || nextId("web"),
            sentAt: new Date().toISOString(),
            payload: payload || null
        });

        return true;
    }

    function dispatchCommand(message) {
        lastCommand = message;
        window.dispatchEvent(new CustomEvent("aiseworks:desktop-command", { detail: message }));

        handlers.forEach((handler) => {
            try {
                handler(message);
            } catch {
                // Desktop bridge handlers must not break the host channel.
            }
        });
    }

    function completePendingRequest(message) {
        if (!message.replyTo || !pendingRequests.has(message.replyTo)) {
            return false;
        }

        const pending = pendingRequests.get(message.replyTo);
        pendingRequests.delete(message.replyTo);
        window.clearTimeout(pending.timeout);
        pending.resolve(message);
        return true;
    }

    function receiveHostMessage(event) {
        const message = normalizeMessage(event.data);
        if (!message
            || message.channel !== channel
            || message.direction !== "shell-to-web") {
            return;
        }

        if (completePendingRequest(message)) {
            return;
        }

        if (message.type === "shell.ping") {
            post("web.pong", {
                receivedMessageId: message.id || null,
                title: document.title,
                url: window.location.href
            });
        }

        if (message.type === "shell.context") {
            window.AiseworksDesktopBridge.context = message.payload || null;
            post("web.context.received", {
                receivedMessageId: message.id || null,
                url: window.location.href
            });
        }

        dispatchCommand(message);
    }

    function request(type, payload, timeoutMs) {
        if (!hasHostBridge()) {
            return Promise.reject(new Error("Aiseworks desktop bridge is not available."));
        }

        const id = nextId("web-request");
        const timeout = window.setTimeout(() => {
            if (!pendingRequests.has(id)) {
                return;
            }

            const pending = pendingRequests.get(id);
            pendingRequests.delete(id);
            pending.reject(new Error(`Desktop bridge request timed out: ${type}`));
        }, timeoutMs || 5000);

        const promise = new Promise((resolve, reject) => {
            pendingRequests.set(id, { resolve, reject, timeout });
        });

        post(type, payload, id);
        return promise;
    }

    window.AiseworksDesktopBridge = {
        version: "0.1",
        isAvailable: hasHostBridge,
        context: null,
        postEvent: post,
        request,
        requestSystemStatus: function () {
            return request("web.request.systemStatus", {
                title: document.title,
                url: window.location.href
            });
        },
        onCommand: function (handler) {
            handlers.add(handler);
            return function unsubscribe() {
                handlers.delete(handler);
            };
        },
        getLastCommand: function () {
            return lastCommand;
        }
    };

    if (hasHostBridge()) {
        markDesktopHost();
        window.chrome.webview.addEventListener("message", receiveHostMessage);
        window.addEventListener("DOMContentLoaded", function () {
            markDesktopHost();
            post("web.ready", {
                title: document.title,
                url: window.location.href,
                userAgent: window.navigator.userAgent
            });
        });
    }
})();
