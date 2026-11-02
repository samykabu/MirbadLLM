// Connection-loss UI (see ReconnectModal.razor): a non-blocking banner first, the blocking dialog only once
// the first reconnect attempt has failed, and no Esc-dismissal of that dialog while disconnected.
const reconnectModal = document.getElementById("components-reconnect-modal");
const banner = document.getElementById("connection-banner");
const bannerAge = banner ? banner.querySelector("[data-conn-age]") : null;

reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

let circuitDown = false;
let browserOffline = typeof navigator !== "undefined" && navigator.onLine === false;
let lostAt = null;
let ageTimer = null;

// The last time live data reached this page (js/shell.js records it from the pages' "Updated" stamps),
// or the moment the connection dropped when the page shows no live data.
function lastUpdate() {
    const live = typeof window.mirbadllmLastLiveUpdate === "function" ? window.mirbadllmLastLiveUpdate() : null;
    return live || lostAt || Date.now();
}

function formatAge(seconds) {
    if (seconds < 2) {
        return "just now";
    }

    if (seconds < 60) {
        return seconds + "s ago";
    }

    const minutes = Math.floor(seconds / 60);
    return minutes < 60 ? minutes + "m " + (seconds % 60) + "s ago" : Math.floor(minutes / 60) + "h " + (minutes % 60) + "m ago";
}

function tickAge() {
    if (bannerAge) {
        bannerAge.textContent = formatAge(Math.max(0, Math.round((Date.now() - lastUpdate()) / 1000)));
    }
}

function syncBanner() {
    if (!banner) {
        return;
    }

    const show = circuitDown || browserOffline;
    if (show && banner.hidden) {
        lostAt = lostAt || Date.now();
        tickAge();
        banner.hidden = false;
        document.documentElement.classList.add("is-disconnected");
        ageTimer = ageTimer || setInterval(tickAge, 1000);
    } else if (!show && !banner.hidden) {
        banner.hidden = true;
        lostAt = null;
        document.documentElement.classList.remove("is-disconnected");
        clearInterval(ageTimer);
        ageTimer = null;
    }
}

function showDialog() {
    if (!reconnectModal.open) {
        reconnectModal.showModal();
    }
}

// The framework's own reconnection handler can leave a previous state's class on the modal when it
// moves to the next one (e.g. "show" lingering once "retrying" starts), which renders two state
// messages at once. Our JS is the single source of truth for this class: every dispatch replaces it
// outright with exactly one state class, so exactly one message can ever match.
const RECONNECT_STATE_CLASSES = {
    show: "components-reconnect-show",
    retrying: "components-reconnect-retrying",
    failed: "components-reconnect-failed",
    paused: "components-reconnect-paused",
    "resume-failed": "components-reconnect-resume-failed",
};

function applyReconnectClass(state) {
    const className = RECONNECT_STATE_CLASSES[state];
    if (className) {
        reconnectModal.className = className;
    }
}

function handleReconnectStateChanged(event) {
    const state = event.detail.state;
    applyReconnectClass(state);
    if (state === "show") {
        // First attempt: banner only; the page stays readable.
        circuitDown = true;
        syncBanner();
    } else if (state === "hide") {
        circuitDown = false;
        syncBanner();
        reconnectModal.close();
    } else if (state === "retrying" || state === "paused" || state === "resume-failed") {
        circuitDown = true;
        syncBanner();
        showDialog();
    } else if (state === "failed") {
        circuitDown = true;
        syncBanner();
        showDialog();
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (state === "rejected") {
        location.reload();
    }
}

// Esc must not dismiss the dialog while disconnected. Chrome only lets 'cancel' be prevented after a user
// activation, so a close that slips through is undone as well.
reconnectModal.addEventListener("cancel", (event) => {
    if (circuitDown) {
        event.preventDefault();
    }
});

reconnectModal.addEventListener("close", () => {
    if (circuitDown) {
        showDialog();
    }
});

// The browser knows it lost the network before SignalR does: show the banner straight away.
window.addEventListener("offline", () => {
    browserOffline = true;
    syncBanner();
});

window.addEventListener("online", () => {
    browserOffline = false;
    syncBanner();
});

syncBanner();

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                circuitDown = false;
                syncBanner();
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        } else {
            circuitDown = false;
            syncBanner();
            reconnectModal.close();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
