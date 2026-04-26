// Citus theme bridge — single owner of the <html> class and the
// `citus-theme` cookie/localStorage. Loaded as an ES module by
// ThemeService.InitializeInteractiveAsync.

const COOKIE = "citus-theme";
const STORAGE = "citus-theme";
const ONE_YEAR = 60 * 60 * 24 * 365;

const Mode = { System: 0, Light: 1, Dark: 2 };

function readCookie() {
    const match = document.cookie.match(/(?:^|;\s*)citus-theme=([^;]+)/);
    return match ? decodeURIComponent(match[1]) : null;
}

function writeCookie(value) {
    document.cookie = `${COOKIE}=${encodeURIComponent(value)}; Path=/; Max-Age=${ONE_YEAR}; SameSite=Lax`;
}

function clearCookie() {
    document.cookie = `${COOKIE}=; Path=/; Max-Age=0; SameSite=Lax`;
}

function modeToText(mode) {
    if (mode === Mode.Dark) return "dark";
    if (mode === Mode.Light) return "light";
    return "system";
}

function textToMode(text) {
    if (text === "dark") return Mode.Dark;
    if (text === "light") return Mode.Light;
    return Mode.System;
}

function isSystemDark() {
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function resolveDark(mode) {
    if (mode === Mode.Dark) return true;
    if (mode === Mode.Light) return false;
    return isSystemDark();
}

function paint(mode) {
    const dark = resolveDark(mode);
    const root = document.documentElement;
    root.classList.toggle("dark", dark);
    root.classList.toggle("light", !dark);
    root.dataset.theme = dark ? "dark" : "light";
    root.style.colorScheme = dark ? "dark" : "light";
    return { Mode: mode, IsDark: dark };
}

let mediaQuery = null;
let mediaListener = null;

function bindSystemListener(currentMode) {
    if (currentMode !== Mode.System) {
        unbindSystemListener();
        return;
    }
    if (mediaQuery) return;
    mediaQuery = window.matchMedia("(prefers-color-scheme: dark)");
    mediaListener = () => paint(Mode.System);
    if (mediaQuery.addEventListener) {
        mediaQuery.addEventListener("change", mediaListener);
    } else {
        mediaQuery.addListener(mediaListener);
    }
}

function unbindSystemListener() {
    if (!mediaQuery || !mediaListener) return;
    if (mediaQuery.removeEventListener) {
        mediaQuery.removeEventListener("change", mediaListener);
    } else {
        mediaQuery.removeListener(mediaListener);
    }
    mediaQuery = null;
    mediaListener = null;
}

export function init(serverMode) {
    let mode = serverMode;
    const stored = window.localStorage?.getItem(STORAGE);
    if (stored) {
        mode = textToMode(stored);
    } else {
        const cookieMode = readCookie();
        if (cookieMode) mode = textToMode(cookieMode);
    }
    const state = paint(mode);
    bindSystemListener(mode);
    return state;
}

export function set(mode) {
    const text = modeToText(mode);
    if (mode === Mode.System) {
        clearCookie();
        window.localStorage?.removeItem(STORAGE);
    } else {
        writeCookie(text);
        try { window.localStorage?.setItem(STORAGE, text); } catch { /* private mode */ }
    }
    const state = paint(mode);
    bindSystemListener(mode);
    return state;
}

// Expose for an inline pre-hydration script in the document head if needed.
window.__citusTheme = { init, set };
