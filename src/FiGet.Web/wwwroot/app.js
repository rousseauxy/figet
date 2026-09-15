// The only script these pages load. They are statically rendered, so everything here is a delegated
// listener on the document: no framework, no per-element wiring, nothing to initialise after a navigation.
(function () {
    "use strict";

    // ── Tooltips ─────────────────────────────────────────────────────────────────────────────────
    // Every title attribute becomes a tooltip drawn in the page's own style, instead of the browser's plain box.
    // Nothing in the markup changes: while an element is pointed at or focused its title is moved aside, so the
    // browser does not show its own tooltip on top, and it is put back afterwards. One element in <body>,
    // positioned from the target's rectangle, so a table that scrolls sideways cannot clip it.

    var tooltip = (function () {
        var tip = null;
        var target = null;
        var timer = 0;

        // Until when a message from say() is kept up. Copying without the clipboard API focuses and selects a
        // hidden textarea, and the focus change and the scroll that follows would take the "Copied" straight down.
        var holdUntil = 0;

        function hideUnlessHeld() {
            if (Date.now() >= holdUntil) {
                hide();
            }
        }

        function element() {
            if (!tip) {
                tip = document.createElement("div");
                tip.className = "fg-tooltip";
                tip.setAttribute("role", "tooltip");
                tip.hidden = true;
                document.body.appendChild(tip);
            }

            return tip;
        }

        function place() {
            if (!target || !tip || tip.hidden) {
                return;
            }

            var rect = target.getBoundingClientRect();
            var box = tip.getBoundingClientRect();
            var gap = 8;
            var above = rect.top - box.height - gap >= 0;
            var top = above ? rect.top - box.height - gap : rect.bottom + gap;
            var left = rect.left + rect.width / 2 - box.width / 2;
            left = Math.max(6, Math.min(left, document.documentElement.clientWidth - box.width - 6));

            tip.classList.toggle("fg-tooltip-below", !above);
            tip.style.top = (top + window.scrollY) + "px";
            tip.style.left = (left + window.scrollX) + "px";
            tip.style.setProperty("--fg-tooltip-arrow", (rect.left + rect.width / 2 - left) + "px");
        }

        function show(el, text) {
            var node = element();
            node.textContent = text;
            node.hidden = false;
            place();
            node.classList.add("fg-tooltip-on");
        }

        function hide() {
            window.clearTimeout(timer);
            if (target && target.hasAttribute("data-fg-title")) {
                target.setAttribute("title", target.getAttribute("data-fg-title"));
                target.removeAttribute("data-fg-title");
                target.removeAttribute("aria-describedby");
            }

            target = null;
            if (tip) {
                tip.classList.remove("fg-tooltip-on");
                tip.hidden = true;
            }
        }

        function enter(el, delay) {
            if (el === target) {
                return;
            }

            hide();
            var text = el.getAttribute("title");
            if (!text) {
                return;
            }

            target = el;
            el.setAttribute("data-fg-title", text);
            el.removeAttribute("title");
            timer = window.setTimeout(function () {
                if (target === el) {
                    show(el, text);
                }
            }, delay);
        }

        document.addEventListener("mouseover", function (event) {
            var el = event.target.closest ? event.target.closest("[title]") : null;
            if (el) {
                enter(el, 250);
            } else if (target && !target.contains(event.target)) {
                hide();
            }
        });

        document.addEventListener("focusin", function (event) {
            var el = event.target.closest ? event.target.closest("[title]") : null;
            if (el && el.matches(":focus-visible")) {
                enter(el, 0);
            }
        });

        // Leaving the window from inside the element fires no mouseover anywhere else.
        document.addEventListener("mouseout", function (event) {
            if (!event.relatedTarget) {
                hide();
            }
        });
        document.addEventListener("focusout", hideUnlessHeld);
        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape") {
                hide();
            }
        });
        window.addEventListener("scroll", hideUnlessHeld, true);
        window.addEventListener("resize", hideUnlessHeld);

        return {
            // Shows a message on an element for a moment, or goes back to its own title when text is null.
            say: function (el, text) {
                var own = el.getAttribute("data-fg-title") || el.getAttribute("title") || "";
                if (text === null) {
                    if (target === el && tip && !tip.hidden) {
                        tip.textContent = own;
                        place();
                    }

                    return;
                }

                if (target !== el) {
                    enter(el, 0);
                }

                window.clearTimeout(timer);
                holdUntil = Date.now() + 600;
                show(el, text);
            },
        };
    })();

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
    // the tooltip and the accessible name for as long as it is shown, so a screen reader hears it too.
    function flash(button, copied) {
        var label = button.getAttribute("data-label") || button.textContent;
        var name = button.getAttribute("data-name") || button.getAttribute("aria-label") || "";
        button.setAttribute("data-label", label);
        button.setAttribute("data-name", name);

        var outcome = copied ? "Copied" : "Press Ctrl+C";
        button.textContent = outcome;
        button.setAttribute("aria-label", outcome);
        button.classList.add(copied ? "copied" : "copy-failed");
        tooltip.say(button, outcome);
        window.setTimeout(function () {
            button.textContent = label;
            button.setAttribute("aria-label", name);
            button.classList.remove("copied", "copy-failed");
            tooltip.say(button, null);
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

    // ── Buttons that take a while ────────────────────────────────────────────────────────────────
    // A pull fetches a package and everything it depends on before the page comes back, which is seconds for a
    // module with dependencies. Without a sign the click looked like it did nothing, and a second click started the
    // same pull again. The button that sent a form marked data-busy shows a spinner and is disabled until the next
    // page arrives. Disabled after the submit event, not before it, so the form is still sent.

    document.addEventListener("submit", function (event) {
        var form = event.target;
        if (!form.hasAttribute || !form.hasAttribute("data-busy")) {
            return;
        }

        var button = event.submitter || form.querySelector("button[type=submit]");
        if (!button) {
            return;
        }

        if (button.classList.contains("is-busy")) {
            event.preventDefault();
            return;
        }

        button.classList.add("is-busy");
        button.setAttribute("aria-busy", "true");
        var label = form.getAttribute("data-busy");
        if (label) {
            button.setAttribute("title", label);
        }

        window.setTimeout(function () { button.disabled = true; }, 0);
    });

    // A page restored from the back-forward cache comes back with its buttons still spinning.
    window.addEventListener("pageshow", function (event) {
        if (!event.persisted) {
            return;
        }

        document.querySelectorAll("button.is-busy").forEach(function (button) {
            button.classList.remove("is-busy");
            button.removeAttribute("aria-busy");
            button.disabled = false;
        });
    });

    // ── Filters that submit themselves ───────────────────────────────────────────────────────────
    // A filter inside a GET form would otherwise need the Search button pressed after choosing, which
    // reads as broken. Without script the form still works; it just takes the extra press.

    document.addEventListener("change", function (event) {
        var select = event.target.closest("select[data-autosubmit], input[type=checkbox][data-autosubmit]");
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

    // ── Modal dialogs ───────────────────────────────────────────────────────────────────────────
    // A link carrying data-dialog-open="id" opens that <dialog> as a modal: centred, the page behind it darkened and inert,
    // Escape to close. Without script the link simply goes where its href says. A dialog rendered with
    // data-open-on-load - a refused form coming back - opens as soon as the page loads, with the reason inside.
    // A click on the darkened backdrop closes it, as does a button carrying data-dialog-close.

    function openDialog(dialog) {
        if (!dialog || typeof dialog.showModal !== "function" || dialog.open) {
            return false;
        }

        dialog.showModal();
        var first = dialog.querySelector("input:not([type=hidden]), select, textarea");
        if (first) {
            first.focus();
        }

        return true;
    }

    document.addEventListener("click", function (event) {
        var opener = event.target.closest ? event.target.closest("[data-dialog-open]") : null;
        if (opener) {
            if (openDialog(document.getElementById(opener.getAttribute("data-dialog-open")))) {
                event.preventDefault();
            }

            return;
        }

        var closer = event.target.closest ? event.target.closest("[data-dialog-close]") : null;
        if (closer) {
            var owner = closer.closest("dialog");
            if (owner) {
                owner.close();
            }

            return;
        }

        // A click on the dialog element itself, outside its box, is a click on the backdrop.
        if (event.target.tagName === "DIALOG" && event.target.open) {
            var box = event.target.getBoundingClientRect();
            if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) {
                event.target.close();
            }
        }
    });

    var toOpen = document.querySelectorAll("dialog[data-open-on-load]");
    for (var d = 0; d < toOpen.length; d++) {
        openDialog(toOpen[d]);
    }

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
    // A form carrying data-confirm posts only after the reader agrees, in a dialog of the page's own rather than the
    // browser's box: the question, and a button named for what it does (data-confirm-button). With
    // data-confirm-count="field" the question starts with how many of that field's checkboxes are ticked. Without script
    // the form posts at once, which is why only signed-in people ever see such a form.
    //
    // In the capture phase, so it runs before the busy spinner's own submit handler: a question the reader cancels must
    // not leave a spinning, disabled button behind.

    var confirmDialog = null;

    function confirmElement() {
        if (!confirmDialog) {
            confirmDialog = document.createElement("dialog");
            confirmDialog.className = "fg-modal fg-confirm";
            confirmDialog.setAttribute("aria-labelledby", "fg-confirm-text");
            confirmDialog.innerHTML =
                "<p id=\"fg-confirm-text\" class=\"fg-confirm-text\"></p>" +
                "<div class=\"fg-form-actions\">" +
                "<button type=\"button\" class=\"fg-btn fg-btn-danger\" data-confirm-ok></button>" +
                "<button type=\"button\" class=\"fg-btn\" data-dialog-close>Cancel</button>" +
                "</div>";
            document.body.appendChild(confirmDialog);
        }

        return confirmDialog;
    }

    function selectedCount(form, field) {
        var id = form.getAttribute("id");
        var boxes = document.querySelectorAll("input[type=checkbox][name='" + field + "']");
        var count = 0;
        for (var i = 0; i < boxes.length; i++) {
            if (boxes[i].checked && (boxes[i].form === form || (id && boxes[i].getAttribute("form") === id))) {
                count++;
            }
        }

        return count;
    }

    document.addEventListener("submit", function (event) {
        var form = event.target;
        if (!form.hasAttribute || !form.hasAttribute("data-confirm")) {
            return;
        }

        if (form.getAttribute("data-confirmed") === "true") {
            form.removeAttribute("data-confirmed");
            return;
        }

        var dialog = confirmElement();
        if (typeof dialog.showModal !== "function") {
            if (!window.confirm(form.getAttribute("data-confirm"))) {
                event.preventDefault();
                event.stopImmediatePropagation();
            }

            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();

        var text = form.getAttribute("data-confirm");
        var countField = form.getAttribute("data-confirm-count");
        if (countField) {
            var count = selectedCount(form, countField);
            text = count + " selected. " + text;
        }

        var submitter = event.submitter || null;
        dialog.querySelector(".fg-confirm-text").textContent = text;
        var ok = dialog.querySelector("[data-confirm-ok]");
        ok.textContent = form.getAttribute("data-confirm-button") || (submitter && submitter.textContent.trim()) || "Confirm";
        ok.onclick = function () {
            dialog.close();
            form.setAttribute("data-confirmed", "true");
            if (typeof form.requestSubmit === "function") {
                form.requestSubmit(submitter && submitter.form === form ? submitter : undefined);
            } else {
                form.submit();
            }
        };

        dialog.showModal();
        dialog.querySelector("[data-dialog-close]").focus();
    }, true);

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

    // One queue per zone, worked one job at a time. Files dropped or picked while something is uploading join the
    // end of it and take their turn - a 400 MB installer used to make every drop after it vanish without a word -
    // and an archive import waits in the same line. The listing is server-rendered, so it is reloaded once the
    // queue is empty and no row is still asking whether to replace a file.

    var queues = new WeakMap();

    function queueOf(zone) {
        var state = queues.get(zone);
        if (!state) {
            state = { jobs: [], running: false, changed: false, asking: 0 };
            queues.set(zone, state);
        }

        return state;
    }

    function replaceWanted(zone) {
        var box = zone.querySelector("input[data-asset-overwrite]");
        return !!(box && box.checked);
    }

    function enqueue(zone, job) {
        var state = queueOf(zone);
        state.jobs.push(job);
        if (!state.running) {
            state.running = true;
            zone.classList.add("fg-uploading");
            runNext(zone, state);
        }
    }

    function settle(zone, state) {
        if (state.jobs.length > 0) {
            runNext(zone, state);
            return;
        }

        state.running = false;
        zone.classList.remove("fg-uploading");
        if (state.changed && state.asking === 0) {
            window.location.reload();
        }
    }

    function runNext(zone, state) {
        var job = state.jobs.shift();
        var row = job.row || progressRow(zone, job.file);
        var status = row.querySelector("[data-status]");
        var finished;
        if (job.kind === "import") {
            finished = importArchive(zone, job.file, job.overwrite, row).then(function (changed) {
                state.changed = state.changed || changed;
            });
        } else {
            finished = uploadOne(zone, job.file, job.overwrite, row).then(function (result) {
                if (result.status === 201) {
                    state.changed = true;
                    status.textContent = job.overwrite ? "Replaced" : "Done";
                    row.classList.add("fg-upload-ok");
                } else if (result.status === 409 && !job.overwrite) {
                    askToReplace(zone, state, job, row);
                } else if (result.status === 409) {
                    status.textContent = "Kept the existing file";
                } else {
                    status.textContent = result.message || ("Failed (" + result.status + ")");
                    row.classList.add("fg-upload-failed");
                }
            });
        }

        finished.then(function () {
            settle(zone, state);
        });
    }

    // The question is asked in the file's own row, with two buttons, not in a browser dialog: the other files carry
    // on meanwhile, and a person can leave a row unanswered for as long as they like.
    function askToReplace(zone, state, job, row) {
        var status = row.querySelector("[data-status]");
        state.asking++;
        status.textContent = "Already exists here.";
        var choice = document.createElement("span");
        choice.className = "fg-upload-choice";
        var replace = document.createElement("button");
        replace.type = "button";
        replace.className = "fg-btn fg-btn-sm fg-btn-danger";
        replace.textContent = "Replace";
        var keep = document.createElement("button");
        keep.type = "button";
        keep.className = "fg-btn fg-btn-sm";
        keep.textContent = "Keep";
        choice.appendChild(replace);
        choice.appendChild(keep);
        status.appendChild(choice);

        replace.addEventListener("click", function () {
            state.asking--;
            status.textContent = "Waiting";
            enqueue(zone, { kind: "upload", file: job.file, overwrite: true, row: row });
        });

        keep.addEventListener("click", function () {
            state.asking--;
            status.textContent = "Kept the existing file";
            if (!state.running) {
                settle(zone, state);
            }
        });
    }

    function uploadAll(zone, files) {
        if (!files || files.length === 0) {
            return;
        }

        var overwrite = replaceWanted(zone);
        for (var i = 0; i < files.length; i++) {
            enqueue(zone, { kind: "upload", file: files[i], overwrite: overwrite });
        }
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

    function importArchive(zone, file, overwrite, row) {
        // Resolved with whether the listing changed, so the queue knows whether a reload is due.
        var finished;
        var done = new Promise(function (resolve) {
            finished = resolve;
        });
        var name = file.name.toLowerCase();
        var format = /\.zip$/.test(name) ? "zip" : (/\.(tgz|tar\.gz)$/.test(name) ? "tgz" : null);
        var status = row.querySelector("[data-status]");
        if (!format) {
            status.textContent = "Not a .zip, .tgz or .tar.gz file";
            row.classList.add("fg-upload-failed");
            finished(false);
            return done;
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
                finished(false);
                return;
            }

            var failed = result.failed || [];
            status.textContent = "Imported " + result.imported + ", skipped " + result.skipped + (failed.length ? ", failed " + failed.length : "")
                + (request.status === 413 ? " (stopped: larger than this server accepts)" : "");
            if (failed.length || request.status !== 200) {
                // The failure list stays on screen: a reload would lose it, so an import with failures is not
                // counted as a change even when part of it went in.
                row.classList.add("fg-upload-failed");
                var details = document.createElement("span");
                details.className = "fg-small fg-muted";
                details.textContent = failed.join("; ");
                row.appendChild(details);
                finished(false);
            } else {
                row.classList.add("fg-upload-ok");
                finished(result.imported > 0);
            }
        });

        request.addEventListener("error", function () {
            status.textContent = "The connection failed.";
            row.classList.add("fg-upload-failed");
            finished(false);
        });

        status.textContent = "Uploading";
        request.send(file);
        return done;
    }

    document.addEventListener("change", function (event) {
        var input = event.target.closest ? event.target.closest("input[data-asset-archive]") : null;
        var zone = input ? input.closest("[data-asset-upload]") : null;
        if (zone && input.files && input.files.length) {
            enqueue(zone, { kind: "import", file: input.files[0], overwrite: replaceWanted(zone) });
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
