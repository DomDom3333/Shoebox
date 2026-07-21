// Light / dark toggle. The theme is resolved server-side from the
// "shoebox-theme" cookie (see _Layout.cshtml), so this only wires the button
// and writes the same cookie back so the choice survives navigation.
(function () {
  "use strict";
  var root = document.documentElement;
  var btn = document.getElementById("theme-toggle");
  if (!btn) return;

  function sync() {
    var dark = root.dataset.theme === "dark";
    btn.setAttribute("aria-pressed", String(dark));
    btn.setAttribute("aria-label", dark ? "Switch to light mode" : "Switch to dark mode");
  }

  function persist(theme) {
    // One year, site-wide, sent on same-site navigations so the server can
    // render the chosen theme on the next page.
    document.cookie = "shoebox-theme=" + theme + "; path=/; max-age=31536000; SameSite=Lax";
  }

  sync();

  btn.addEventListener("click", function () {
    var next = root.dataset.theme === "dark" ? "light" : "dark";
    root.dataset.theme = next;
    persist(next);
    sync();
  });
})();
