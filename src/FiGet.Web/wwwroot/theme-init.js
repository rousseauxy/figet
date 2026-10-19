// Applies a recorded light or dark choice before the first paint. Loaded from <head> as a blocking script, not
// deferred and not from app.js at the end of <body>: anything that runs after the page is drawn makes every page
// flash the other theme. A file of its own rather than an inline script, so the content policy can say
// script-src 'self' with no hash to keep in step. Only an explicit choice is recorded here: with nothing stored,
// the stylesheet's media query answers the operating system on its own.
(function () {
    "use strict";

    try {
        var chosen = localStorage.getItem("figet-theme");
        if (chosen === "light" || chosen === "dark") {
            document.documentElement.setAttribute("data-theme", chosen);
        }
    } catch (error) {
        // A browser that refuses storage simply follows the system preference.
    }
})();
