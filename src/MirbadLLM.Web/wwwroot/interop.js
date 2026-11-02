// Small JS interop helpers for the interactive pages: clipboard copy, autoscroll, chat Enter-to-send and
// the <dialog> element behind ConfirmDialog. Theme, drawer, shortcuts and tab keyboard focus live in js/shell.js.

// The async Clipboard API exists only in secure contexts (HTTPS or localhost). The box serves plain HTTP on the LAN,
// so there it falls back to selecting a temporary textarea and execCommand('copy'), which browsers still allow
// during the click's user activation. The textarea goes inside an open modal <dialog> when there is one: everything
// outside a modal dialog is inert and could not be selected.
export async function copyText(text) {
    if (window.isSecureContext && navigator.clipboard?.writeText) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Denied or unavailable (e.g. the document lost focus): try the fallback below.
        }
    }

    return copyWithSelection(text);
}

function copyWithSelection(text) {
    const previous = document.activeElement;
    const host = previous?.closest?.('dialog[open]') ?? document.querySelector('dialog[open]') ?? document.body;
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.setAttribute('aria-hidden', 'true');
    area.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;padding:0;border:0;opacity:0;pointer-events:none;';
    host.appendChild(area);
    let ok = false;
    try {
        area.focus({ preventScroll: true });
        area.select();
        area.setSelectionRange(0, text.length);
        ok = document.execCommand('copy');
    } catch {
        ok = false;
    } finally {
        area.remove();
        if (previous instanceof HTMLElement) {
            previous.focus({ preventScroll: true });
        }
    }

    return ok;
}

export function scrollToBottom(elementId) {
    const el = document.getElementById(elementId);
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
}

export function showNodeHealth() {
    const heading = document.getElementById('node-attention-title');
    if (!heading) return;
    // The status bar wraps on phones; its token is only the minimum height.
    heading.style.scrollMarginTop = `${(document.querySelector('.statusbar')?.getBoundingClientRect().height ?? 0) + 16}px`;
    heading.scrollIntoView({ block: 'start' });
    heading.focus({ preventScroll: true });
}

// Chat page: Enter sends, Shift+Enter inserts a newline. Blazor's declarative preventDefault applies to
// every keydown on the element, so plain Enter-only prevention has to happen here in JS.
export function bindEnterToSend(textareaId, dotnetHelper, methodName) {
    const el = document.getElementById(textareaId);
    if (!el || el.dataset.enterBound) {
        return;
    }

    el.dataset.enterBound = '1';
    el.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            // Pass the text along: Enter is pressed before the field loses focus, so the server-side copy of
            // the value can still be stale.
            dotnetHelper.invokeMethodAsync(methodName, el.value);
        }
    });
}

// Modal <dialog>: showModal() gives focus containment, Esc (as a 'cancel' event) and an inert background.
export function showDialog(el) {
    if (el && !el.open && typeof el.showModal === 'function') {
        el.showModal();
    }
}

export function closeDialog(el) {
    if (el && el.open) {
        el.close();
    }
}

// Backup page: download a file the server prepared under a one-time token, without leaving the page.
export function downloadUrl(url, fileName) {
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName || '';
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
    return true;
}
