// Light / dark toggle. The initial theme is resolved inline in <head> before
// first paint; this only wires the button and persists the choice.
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

  sync();

  btn.addEventListener("click", function () {
    var next = root.dataset.theme === "dark" ? "light" : "dark";
    root.dataset.theme = next;
    try { localStorage.setItem("shoebox-theme", next); } catch (e) { /* ignore */ }
    sync();
  });
})();
