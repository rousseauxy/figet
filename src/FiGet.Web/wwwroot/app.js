// Copy-to-clipboard for any button carrying data-copy. The pages are statically rendered, so this is the
// only script on them. It must work on plain HTTP too: navigator.clipboard exists only in a secure context,
// and an instance reached over http:// on a LAN is not one, hence the textarea fallback.
(function () {
    "use strict";

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
})();
