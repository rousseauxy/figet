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

    // The button is an icon; its word is hidden and only shown when copying failed. The outcome also goes into
    // the title and the accessible name for as long as it is shown, so a screen reader hears it too.
    function flash(button, copied) {
        var label = button.getAttribute("data-label") || button.textContent;
        var name = button.getAttribute("data-name") || button.getAttribute("aria-label") || "";
        var title = button.getAttribute("data-title") || button.getAttribute("title") || "";
        button.setAttribute("data-label", label);
        button.setAttribute("data-name", name);
        button.setAttribute("data-title", title);

        var outcome = copied ? "Copied" : "Press Ctrl+C";
        button.textContent = outcome;
        button.setAttribute("aria-label", outcome);
        button.setAttribute("title", outcome);
        button.classList.add(copied ? "copied" : "copy-failed");
        window.setTimeout(function () {
            button.textContent = label;
            button.setAttribute("aria-label", name);
            button.setAttribute("title", title);
            button.classList.remove("copied", "copy-failed");
        }, copied ? 1200 : 2500);
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
                function () { flash(button, true); },
                function () { flash(button, fallbackCopy(text)); });
            return;
        }

        flash(button, fallbackCopy(text));
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
    //
    // It has to be re-applied after an enhanced navigation too. For a signed-in reader the framework is
    // loaded, so following a link patches the DOM instead of loading a page: the <head> script never runs
    // again and the attribute it set does not survive the patch. That is why a chosen theme used to last
    // exactly one page, and why an anonymous reader - who gets no framework at all - never saw it happen.

    function storedTheme() {
        try {
            var chosen = localStorage.getItem("figet-theme");
            return chosen === "light" || chosen === "dark" ? chosen : null;
        } catch (error) {
            // A browser refusing storage simply follows the system preference.
            return null;
        }
    }

    function currentTheme() {
        var attribute = document.documentElement.getAttribute("data-theme");
        if (attribute === "light" || attribute === "dark") {
            return attribute;
        }

        // No choice recorded, so the reader is seeing whatever the system asked for.
        return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    }

    // Static SSR cannot know the reader's choice, so the button ships a neutral glyph and is painted
    // here. It names what the click will do rather than what is on screen, which is what a reader is
    // actually choosing between.
    function paintToggles() {
        var dark = currentTheme() === "dark";
        var label = dark ? "Switch to light" : "Switch to dark";
        var buttons = document.querySelectorAll("[data-theme-toggle]");
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].textContent = dark ? "☀" : "☾";
            buttons[i].setAttribute("title", label);
            buttons[i].setAttribute("aria-label", label);
        }
    }

    function applyTheme() {
        var chosen = storedTheme();
        if (chosen) {
            document.documentElement.setAttribute("data-theme", chosen);
        }

        paintToggles();
    }

    document.addEventListener("click", function (event) {
        var toggle = event.target.closest("[data-theme-toggle]");
        if (!toggle) {
            return;
        }

        var next = currentTheme() === "dark" ? "light" : "dark";
        document.documentElement.setAttribute("data-theme", next);
        try {
            localStorage.setItem("figet-theme", next);
        } catch (error) {
            // A browser refusing storage still gets the theme it asked for, just not next time.
        }

        paintToggles();
    });

    applyTheme();

    // The framework script is loaded after this one, so the hook is registered once it exists. Absent
    // for an anonymous reader, which is correct: without the framework there is no enhanced navigation.
    document.addEventListener("DOMContentLoaded", function () {
        if (window.Blazor && typeof window.Blazor.addEventListener === "function") {
            window.Blazor.addEventListener("enhancedload", applyTheme);
        }
    });

    // ── The signed-in menu ───────────────────────────────────────────────────────────────────────
    // Opening the menu is the browser's job - it is a <details> so that it works with no script at all.
    // Closing it when the reader looks elsewhere is not something <details> does on its own.

    function closeMenus(except) {
        var menus = document.querySelectorAll("details[data-nav-menu][open]");
        for (var i = 0; i < menus.length; i++) {
            if (menus[i] !== except) {
                menus[i].removeAttribute("open");
            }
        }
    }

    document.addEventListener("click", function (event) {
        var inside = event.target.closest ? event.target.closest("details[data-nav-menu]") : null;
        closeMenus(inside);
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            closeMenus(null);
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

    // ── Buttons that ask first ───────────────────────────────────────────────────────────────────
    // A form carrying data-confirm posts only after the reader agrees. Without script it posts at once,
    // which is why only admins ever see such a form.

    document.addEventListener("submit", function (event) {
        var form = event.target.closest ? event.target.closest("form[data-confirm]") : null;
        if (form && !window.confirm(form.getAttribute("data-confirm"))) {
            event.preventDefault();
        }
    });

    // ── Uploading into an asset directory ────────────────────────────────────────────────────────
    // One request per file, one file at a time, the file itself as the body: that is what lets a gigabyte
    // installer through without the browser or the server holding it in memory, and it is the same way a
    // script uploads. The antiforgery token travels in a header because the body is taken.
    //
    // A file that already exists is not replaced silently. The server refuses the first attempt with 409,
    // and only a confirmed second attempt asks it to replace.

    function uploadOne(zone, file, overwrite, row) {
        return new Promise(function (resolve) {
            var folder = zone.getAttribute("data-folder") || "";
            var path = folder ? folder + "/" + file.name : file.name;
            var url = zone.getAttribute("data-upload-url") + "?path=" + encodeURIComponent(path) + (overwrite ? "&overwrite=true" : "");
            var token = zone.querySelector("input[name='__RequestVerificationToken']");
            var bar = row.querySelector("progress");
            var status = row.querySelector("[data-status]");

            var request = new XMLHttpRequest();
            request.open("POST", url);
            if (token) {
                request.setRequestHeader("RequestVerificationToken", token.value);
            }
            if (file.type) {
                request.setRequestHeader("Content-Type", file.type);
            }

            request.upload.addEventListener("progress", function (progress) {
                if (progress.lengthComputable) {
                    bar.max = progress.total;
                    bar.value = progress.loaded;
                }
            });

            request.addEventListener("load", function () {
                var message = "";
                try {
                    message = JSON.parse(request.responseText).error || "";
                } catch (error) {
                    message = "";
                }
                resolve({ status: request.status, message: message });
            });

            request.addEventListener("error", function () {
                resolve({ status: 0, message: "The connection failed." });
            });

            status.textContent = "Uploading";
            request.send(file);
        });
    }

    function progressRow(zone, file) {
        var list = zone.querySelector("[data-asset-progress]");
        var row = document.createElement("li");
        var name = document.createElement("span");
        var bar = document.createElement("progress");
        var status = document.createElement("span");
        name.className = "fg-upload-name";
        name.textContent = file.name;
        bar.max = 1;
        bar.value = 0;
        status.setAttribute("data-status", "");
        status.className = "fg-small";
        status.textContent = "Waiting";
        row.appendChild(name);
        row.appendChild(bar);
        row.appendChild(status);
        list.appendChild(row);
        return row;
    }

    function uploadAll(zone, files) {
        if (!files || files.length === 0 || zone.classList.contains("fg-uploading")) {
            return;
        }

        zone.classList.add("fg-uploading");
        var queue = Array.prototype.slice.call(files);
        var changed = false;

        function next() {
            var file = queue.shift();
            if (!file) {
                zone.classList.remove("fg-uploading");
                if (changed) {
                    // The listing is server-rendered; a reload is the honest way to show what is there now.
                    window.location.reload();
                }
                return;
            }

            var row = progressRow(zone, file);
            var status = row.querySelector("[data-status]");
            uploadOne(zone, file, false, row).then(function (result) {
                if (result.status === 409 && window.confirm("\"" + file.name + "\" already exists here. Replace it?")) {
                    return uploadOne(zone, file, true, row);
                }
                return result;
            }).then(function (result) {
                if (result.status === 201) {
                    changed = true;
                    status.textContent = "Done";
                    row.classList.add("fg-upload-ok");
                } else if (result.status === 409) {
                    status.textContent = "Kept the existing file";
                } else {
                    status.textContent = result.message || ("Failed (" + result.status + ")");
                    row.classList.add("fg-upload-failed");
                }
                next();
            });
        }

        next();
    }

    document.addEventListener("change", function (event) {
        var input = event.target.closest ? event.target.closest("input[data-asset-files]") : null;
        var zone = input ? input.closest("[data-asset-upload]") : null;
        if (zone) {
            uploadAll(zone, input.files);
            input.value = "";
        }
    });

    // An archive is imported in one request, body and all, and unpacked by the server into this folder. What
    // it did comes back as counts; a failure list stays on screen instead of being lost to a reload.

    function importArchive(zone, file, overwrite) {
        var name = file.name.toLowerCase();
        var format = /\.zip$/.test(name) ? "zip" : (/\.(tgz|tar\.gz)$/.test(name) ? "tgz" : null);
        var row = progressRow(zone, file);
        var status = row.querySelector("[data-status]");
        if (!format) {
            status.textContent = "Not a .zip, .tgz or .tar.gz file";
            row.classList.add("fg-upload-failed");
            return;
        }

        var folder = zone.getAttribute("data-folder") || "";
        var url = zone.getAttribute("data-import-url") + "?format=" + format + "&path=" + encodeURIComponent(folder) + (overwrite ? "&overwrite=true" : "");
        var token = zone.querySelector("input[name='__RequestVerificationToken']");
        var bar = row.querySelector("progress");
        var request = new XMLHttpRequest();
        request.open("POST", url);
        if (token) {
            request.setRequestHeader("RequestVerificationToken", token.value);
        }

        request.upload.addEventListener("progress", function (progress) {
            if (progress.lengthComputable) {
                bar.max = progress.total;
                bar.value = progress.loaded;
                if (progress.loaded === progress.total) {
                    status.textContent = "Unpacking";
                }
            }
        });

        request.addEventListener("load", function () {
            var result = null;
            try {
                result = JSON.parse(request.responseText);
            } catch (error) {
                result = null;
            }

            if (!result || typeof result.imported !== "number") {
                status.textContent = (result && result.error) || ("Failed (" + request.status + ")");
                row.classList.add("fg-upload-failed");
                return;
            }

            var failed = result.failed || [];
            status.textContent = "Imported " + result.imported + ", skipped " + result.skipped + (failed.length ? ", failed " + failed.length : "")
                + (request.status === 413 ? " (stopped: larger than this server accepts)" : "");
            if (failed.length || request.status !== 200) {
                row.classList.add("fg-upload-failed");
                var details = document.createElement("span");
                details.className = "fg-small fg-muted";
                details.textContent = failed.join("; ");
                row.appendChild(details);
            } else {
                row.classList.add("fg-upload-ok");
                window.location.reload();
            }
        });

        request.addEventListener("error", function () {
            status.textContent = "The connection failed.";
            row.classList.add("fg-upload-failed");
        });

        status.textContent = "Uploading";
        request.send(file);
    }

    document.addEventListener("change", function (event) {
        var input = event.target.closest ? event.target.closest("input[data-asset-archive]") : null;
        var zone = input ? input.closest("[data-asset-upload]") : null;
        if (zone && input.files && input.files.length) {
            var overwrite = zone.querySelector("input[data-asset-archive-overwrite]");
            importArchive(zone, input.files[0], !!(overwrite && overwrite.checked));
            input.value = "";
        }
    });

    document.addEventListener("dragover", function (event) {
        var zone = event.target.closest ? event.target.closest("[data-asset-upload]") : null;
        if (zone) {
            event.preventDefault();
            zone.classList.add("fg-dropzone-over");
        }
    });

    document.addEventListener("dragleave", function (event) {
        var zone = event.target.closest ? event.target.closest("[data-asset-upload]") : null;
        if (zone && !zone.contains(event.relatedTarget)) {
            zone.classList.remove("fg-dropzone-over");
        }
    });

    document.addEventListener("drop", function (event) {
        var zone = event.target.closest ? event.target.closest("[data-asset-upload]") : null;
        if (!zone) {
            return;
        }

        event.preventDefault();
        zone.classList.remove("fg-dropzone-over");
        uploadAll(zone, event.dataTransfer ? event.dataTransfer.files : null);
    });
})();
