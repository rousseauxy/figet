// The only script these pages load. They are statically rendered, so everything here is a delegated
// listener on the document: no framework, no per-element wiring, nothing to initialise after a navigation.
(function () {
    "use strict";

    // ── Copy to clipboard ────────────────────────────────────────────────────────────────────────
    // navigator.clipboard exists only in a secure context, and an instance reached over http:// on a
    // LAN is not one, hence the textarea fallback.

    function fallbackCopy(text) {
        var area = document.createElement("textarea");
        area.value = text;
        area.setAttribute("readonly", "");
        area.style.position = "fixed";
        area.style.top = "-1000px";
        document.body.appendChild(area);
        area.select();
        var copied = false;
        try {
            copied = document.execCommand("copy");
        } catch (error) {
            copied = false;
        }
        document.body.removeChild(area);
        return copied;
    }

    function flash(button, text) {
        var original = button.getAttribute("data-label") || button.textContent;
        button.setAttribute("data-label", original);
        button.textContent = text;
        button.classList.add("copied");
        window.setTimeout(function () {
            button.textContent = original;
            button.classList.remove("copied");
        }, 1200);
    }

    document.addEventListener("click", function (event) {
        var button = event.target.closest("button[data-copy]");
        if (!button) {
            return;
        }

        event.preventDefault();
        var text = button.getAttribute("data-copy") || "";

        if (window.isSecureContext && navigator.clipboard) {
            navigator.clipboard.writeText(text).then(
                function () { flash(button, "Copied"); },
                function () { flash(button, fallbackCopy(text) ? "Copied" : "Press Ctrl+C"); });
            return;
        }

        flash(button, fallbackCopy(text) ? "Copied" : "Press Ctrl+C");
    });

    // ── Filters that submit themselves ───────────────────────────────────────────────────────────
    // A filter inside a GET form would otherwise need the Search button pressed after choosing, which
    // reads as broken. Without script the form still works; it just takes the extra press.

    document.addEventListener("change", function (event) {
        var select = event.target.closest("select[data-autosubmit]");
        if (select && select.form) {
            select.form.submit();
        }
    });

    // ── Light and dark ───────────────────────────────────────────────────────────────────────────
    // The stylesheet already answers the operating system's preference on its own; this only records an
    // explicit choice, which has to beat it. The matching attribute is applied in <head> before first
    // paint, or the page would flash the other theme on every navigation.

    document.addEventListener("click", function (event) {
        var toggle = event.target.closest("[data-theme-toggle]");
        if (!toggle) {
            return;
        }

        var root = document.documentElement;
        var current = root.getAttribute("data-theme");
        if (!current) {
            // No choice recorded yet, so the reader is seeing whatever the system asked for.
            current = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches
                ? "dark"
                : "light";
        }

        var next = current === "dark" ? "light" : "dark";
        root.setAttribute("data-theme", next);
        try {
            localStorage.setItem("figet-theme", next);
        } catch (error) {
            // A browser refusing storage still gets the theme it asked for, just not next time.
        }
    });

    // ── Icons that fail to load ──────────────────────────────────────────────────────────────────
    // A package icon is a URL chosen by whoever published the package, and it points at somewhere we do
    // not control — often unreachable from an air-gapped install. A broken image should leave no trace
    // rather than a grey placeholder. Capture phase, because "error" does not bubble.

    document.addEventListener("error", function (event) {
        var image = event.target;
        if (image && image.tagName === "IMG" && image.hasAttribute("data-fallback")) {
            image.remove();
        }
    }, true);
})();
