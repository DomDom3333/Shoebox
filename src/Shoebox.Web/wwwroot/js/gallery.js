(function () {
  "use strict";

  const poolCode = location.pathname.split("/")[2];
  const gallery = document.getElementById("gallery");
  const nameInput = document.getElementById("uploader-name");
  const fileInput = document.getElementById("file-input");
  const pickBtn = document.getElementById("pick-files");
  const dropzone = document.getElementById("dropzone");
  const progressList = document.getElementById("upload-progress");
  const filterSelect = document.getElementById("uploader-filter");
  const emptyState = document.getElementById("empty-state");
  const photoCount = document.getElementById("photo-count");
  const peopleCount = document.getElementById("people-count");

  // ---------- Upload ----------

  pickBtn.addEventListener("click", () => {
    if (!requireName()) return;
    fileInput.click();
  });

  fileInput.addEventListener("change", () => {
    uploadFiles([...fileInput.files]);
    fileInput.value = "";
  });

  ["dragenter", "dragover"].forEach((evt) =>
    document.addEventListener(evt, (e) => {
      e.preventDefault();
      dropzone.classList.add("dragover");
    })
  );
  ["dragleave", "drop"].forEach((evt) =>
    document.addEventListener(evt, (e) => {
      e.preventDefault();
      if (evt === "drop" || e.target === document.documentElement) {
        dropzone.classList.remove("dragover");
      }
    })
  );
  document.addEventListener("drop", (e) => {
    const files = [...(e.dataTransfer?.files || [])];
    if (files.length) {
      if (!requireName()) return;
      uploadFiles(files);
    }
  });

  function requireName() {
    const name = nameInput.value.trim();
    if (!name) {
      nameInput.focus();
      nameInput.reportValidity ? nameInput.setCustomValidity("") : null;
      alert("Please enter your name first so everyone knows who the photos are from.");
      return false;
    }
    return true;
  }

  async function uploadFiles(files) {
    for (const file of files) {
      const item = document.createElement("li");
      item.innerHTML = `<span>${escapeHtml(file.name)}</span><span class="status">0%</span>`;
      progressList.appendChild(item);
      const status = item.querySelector(".status");

      try {
        const result = await uploadOne(file, (pct) => (status.textContent = pct + "%"));
        const r = result.results && result.results[0];
        if (r && r.status === "added") {
          status.textContent = "uploaded";
          status.className = "status ok";
          item.classList.add("settled");
          // The photo is rendered and in the box by the time this returns, so ask the
          // live feed for it now: it drops into the gallery while the rest upload.
          refreshSoon(400);
        } else if (r && r.status === "duplicate") {
          status.textContent = "already in box";
          status.className = "status";
          item.classList.add("settled");
        } else {
          status.textContent = (r && r.reason) || "failed";
          status.className = "status fail";
        }
      } catch (err) {
        status.textContent = err.message;
        status.className = "status fail";
      }
    }

    // Nothing reloads this list away any more. What landed is in the gallery to look at;
    // only what went wrong is worth keeping on screen.
    setTimeout(() => progressList.querySelectorAll("li.settled").forEach((li) => li.remove()), 4000);
  }

  function uploadOne(file, onProgress) {
    // XHR instead of fetch: fetch has no upload progress events.
    return new Promise((resolve, reject) => {
      const form = new FormData();
      form.append("uploaderName", nameInput.value.trim());
      form.append("files", file, file.name);

      const xhr = new XMLHttpRequest();
      xhr.open("POST", `/api/p/${poolCode}/media`);
      xhr.upload.addEventListener("progress", (e) => {
        if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100));
      });
      xhr.addEventListener("load", () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          resolve(JSON.parse(xhr.responseText));
        } else if (xhr.status === 401) {
          reject(new Error("box is locked; refresh the page"));
        } else if (xhr.status === 413) {
          reject(new Error("file too large"));
        } else {
          let msg = "upload failed";
          try { msg = JSON.parse(xhr.responseText).error || msg; } catch { /* keep default */ }
          reject(new Error(msg));
        }
      });
      xhr.addEventListener("error", () => reject(new Error("network error")));
      xhr.send(form);
    });
  }

  // ---------- Filter ----------

  filterSelect?.addEventListener("change", applyFilter);

  function applyFilter() {
    const who = filterSelect.value;
    for (const tile of gallery.querySelectorAll(".tile")) {
      tile.style.display = !who || tile.dataset.uploader === who ? "" : "none";
    }
  }

  // ---------- Save to gallery (mobile share sheet) ----------

  // No browser API can write straight to the phone's camera roll, but the Web Share
  // API can hand the image files to the OS share sheet, where the user taps
  // "Save Image(s)" to drop them into Photos/Gallery. This is a phone-only helper:
  // desktop keeps using the ZIP download.
  const saveGalleryBtn = document.getElementById("save-gallery-btn");
  const SAVE_MAX = 50; // above this, the ZIP is a better bet than a huge share payload.

  function canShareFiles() {
    try {
      const probe = new File([""], "probe.jpg", { type: "image/jpeg" });
      return !!(navigator.canShare && navigator.canShare({ files: [probe] }));
    } catch {
      return false;
    }
  }

  // Desktop Chrome/Edge also expose canShare({ files }), so feature detection alone
  // isn't enough — gate on a coarse (touch) pointer too, matching the CSS breakpoints.
  function isTouchDevice() {
    return !!(window.matchMedia && window.matchMedia("(pointer: coarse)").matches);
  }

  // Whether to offer it at all; whether to show it right now also depends on the box
  // holding something, which the live feed can change under the visitor's feet.
  const canSaveToGallery = !!saveGalleryBtn && isTouchDevice() && canShareFiles();
  if (canSaveToGallery) {
    saveGalleryBtn.addEventListener("click", saveToGallery);
  }

  async function saveToGallery() {
    const tiles = [...gallery.querySelectorAll(".tile")];
    if (!tiles.length) return;
    if (tiles.length > SAVE_MAX) {
      alert(
        `That's ${tiles.length} photos — too many to save in one go. ` +
          `Use "Download all photos" to grab them as a ZIP instead.`
      );
      return;
    }

    const label = saveGalleryBtn.textContent;
    saveGalleryBtn.disabled = true;
    saveGalleryBtn.textContent = `Preparing ${tiles.length}…`;
    try {
      // navigator.share() needs live transient activation (~5s). Fetching the
      // originals one-by-one blows past that window, so share() throws
      // NotAllowedError. Fetch them in parallel to keep the wait short.
      let done = 0;
      const files = await Promise.all(
        tiles.map(async (tile, i) => {
          const res = await fetch(tile.dataset.original);
          if (!res.ok) throw new Error("fetch failed");
          const blob = await res.blob();
          saveGalleryBtn.textContent = `Preparing ${++done}/${tiles.length}…`;
          return new File([blob], tile.dataset.filename || `photo-${i + 1}.jpg`, {
            type: blob.type || "image/jpeg",
          });
        })
      );

      if (!navigator.canShare || !navigator.canShare({ files })) {
        throw new Error("unshareable");
      }
      await navigator.share({ files });
    } catch (err) {
      // Tapping "Cancel" on the share sheet rejects with AbortError — not a failure.
      if (!err || err.name !== "AbortError") {
        alert(
          'Couldn\'t hand these to your gallery. You can still use "Download all photos" for the ZIP.'
        );
      }
    } finally {
      saveGalleryBtn.textContent = label;
      saveGalleryBtn.disabled = false;
    }
  }

  // ---------- Animations (GIF / animated WebP) ----------

  // The grid holds still so a box full of GIFs doesn't flicker at everyone. On a device
  // with a real pointer, hovering a tile swaps its still for the animated display proxy;
  // on touch there's no hover, and tapping opens the lightbox, which always plays.
  const hoverPlays = !window.matchMedia || window.matchMedia("(hover: hover)").matches;

  function bindAnimation(tile) {
    if (!hoverPlays || !tile.classList.contains("animated")) return;
    const img = tile.querySelector("img");
    if (!img) return;
    const still = img.src;
    const moving = tile.dataset.original.replace(/\/original$/, "/display");
    tile.addEventListener("mouseenter", () => (img.src = moving));
    tile.addEventListener("mouseleave", () => (img.src = still));
  }

  for (const tile of gallery.querySelectorAll(".tile.animated")) {
    bindAnimation(tile);
  }

  // ---------- Like ----------

  gallery.addEventListener("click", async (e) => {
    const btn = e.target.closest(".like-btn");
    if (!btn) return;
    e.stopPropagation(); // don't open the lightbox
    if (btn.disabled) return;

    const tile = btn.closest(".tile");
    btn.disabled = true;
    try {
      const res = await fetch(`/api/media/${tile.dataset.id}/like`, { method: "POST" });
      if (!res.ok) throw new Error("like failed");
      const data = await res.json();
      setLiked(btn, data.liked, data.count);
    } catch {
      // Leave the button as-is; a failed tap just does nothing.
    } finally {
      btn.disabled = false;
    }
  });

  function setLiked(btn, liked, count) {
    btn.classList.toggle("liked", liked);
    btn.setAttribute("aria-pressed", liked ? "true" : "false");
    btn.title = (liked ? "Unlike" : "Like") + " this photo";
    btn.querySelector(".heart").textContent = liked ? "❤" : "♡";
    btn.querySelector(".like-count").textContent = count;
    if (liked) {
      // Retrigger the pop animation on each fresh like.
      btn.classList.remove("just-liked");
      void btn.offsetWidth;
      btn.classList.add("just-liked");
    }
  }

  // ---------- Delete ----------

  gallery.addEventListener("click", async (e) => {
    const btn = e.target.closest(".delete-btn");
    if (!btn) return;
    e.stopPropagation();
    const tile = btn.closest(".tile");
    if (!confirm("Delete this photo?")) return;

    const res = await fetch(`/api/media/${tile.dataset.id}`, { method: "DELETE" });
    if (res.ok) {
      tile.remove();
      refreshChrome();
    } else {
      alert("Could not delete this photo.");
    }
  });

  // ---------- Lightbox ----------

  const lightbox = document.getElementById("lightbox");
  const lbImg = document.getElementById("lightbox-img");
  const lbCaption = document.getElementById("lightbox-caption");
  const lbDownload = document.getElementById("lightbox-download");
  // The open photo is held as the tile itself, not its index: photos arriving live can
  // slot in ahead of it, and an index would quietly start pointing at someone else's.
  let currentTile = null;

  function visibleTiles() {
    return [...gallery.querySelectorAll(".tile")].filter((t) => t.style.display !== "none");
  }

  function step(delta) {
    const tiles = visibleTiles();
    if (!tiles.length) return;
    const at = currentTile ? tiles.indexOf(currentTile) : -1;
    // The open photo just left the gallery (deleted, or filtered out): start over.
    showTile(at === -1 ? tiles[0] : tiles[(at + delta + tiles.length) % tiles.length]);
  }

  function showTile(tile) {
    if (!tile) return;
    currentTile = tile;
    // The lightbox shows the web-safe "display" proxy: full-screen sharp but far smaller
    // than a 50MB phone original, and viewable in every browser (including HEIC). The
    // Download button always fetches the true original.
    const base = tile.dataset.original.replace(/\/original$/, "");
    const isVideo = tile.classList.contains("video");
    lbImg.src = base + "/display";
    // Videos aren't played in the browser: the lightbox shows the poster frame and the
    // Download button hands over the clip itself.
    lbCaption.textContent =
      `${tile.dataset.uploader} · ${tile.dataset.filename}` + (isVideo ? " · video, download to play" : "");
    lbDownload.textContent = isVideo ? "Download video" : "Download";
    lbDownload.href = tile.dataset.original + "?download=true";
    lightbox.hidden = false;
    document.body.style.overflow = "hidden";
  }

  function closeLightbox() {
    lightbox.hidden = true;
    lbImg.src = "";
    currentTile = null;
    document.body.style.overflow = "";
  }

  gallery.addEventListener("click", (e) => {
    const tile = e.target.closest(".tile");
    if (!tile || e.target.closest(".delete-btn") || e.target.closest(".like-btn")) return;
    showTile(tile);
  });

  lightbox.querySelector(".lb-close").addEventListener("click", closeLightbox);
  lightbox.querySelector(".lb-prev").addEventListener("click", () => step(-1));
  lightbox.querySelector(".lb-next").addEventListener("click", () => step(1));
  lightbox.addEventListener("click", (e) => {
    if (e.target === lightbox) closeLightbox();
  });
  document.addEventListener("keydown", (e) => {
    if (lightbox.hidden) return;
    if (e.key === "Escape") closeLightbox();
    if (e.key === "ArrowLeft") step(-1);
    if (e.key === "ArrowRight") step(1);
  });

  // Touch: swipe left/right to move between photos, quick tap to close.
  let touchX = null, touchY = null, touchMoved = false;
  lightbox.addEventListener("touchstart", (e) => {
    if (e.touches.length !== 1) { touchX = null; return; }
    touchX = e.touches[0].clientX;
    touchY = e.touches[0].clientY;
    touchMoved = false;
  }, { passive: true });
  lightbox.addEventListener("touchmove", () => { touchMoved = true; }, { passive: true });
  lightbox.addEventListener("touchend", (e) => {
    if (touchX === null) return;
    const dx = e.changedTouches[0].clientX - touchX;
    const dy = e.changedTouches[0].clientY - touchY;
    if (Math.abs(dx) > 45 && Math.abs(dx) > Math.abs(dy) * 1.4) {
      step(dx < 0 ? 1 : -1);
    } else if (!touchMoved && e.target === lbImg) {
      closeLightbox();
    }
    touchX = null;
  }, { passive: true });

  // ---------- Share dialog ----------

  const shareDialog = document.getElementById("share-dialog");
  const shareUrl = document.getElementById("share-url");
  document.getElementById("share-btn")?.addEventListener("click", () => shareDialog.showModal());
  document.getElementById("share-close")?.addEventListener("click", () => shareDialog.close());
  document.getElementById("copy-link")?.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(shareUrl.value);
      document.getElementById("copy-link").textContent = "Copied!";
    } catch {
      shareUrl.select();
    }
  });

  // ---------- Live refresh ----------
  //
  // A box fills while people are looking at it, so the page asks every ten seconds for the
  // prints that landed since the ones it drew and slots them in, rather than reloading and
  // losing the visitor's place. Polling, not a held-open socket per viewer: a box is a
  // handful of people for an afternoon. A backgrounded tab asks for nothing.

  const POLL_MS = 10000;
  const QUIET_POLL_MS = 30000;
  const QUIET_AFTER_MS = 300000;
  const POLL_MAX_MS = 120000;

  let lastArrival = Date.now();
  let cursor = Math.max(0, ...[...gallery.children].map((tile) => Number(tile.dataset.uploaded) || 0));
  let pollTimer = null;
  let polling = false;
  let askAgain = false;
  let failures = 0;
  let liveStopped = false;

  function refreshSoon(delay) {
    if (liveStopped) return;
    clearTimeout(pollTimer);
    pollTimer = setTimeout(refresh, delay);
  }

  function scheduleNext() {
    if (failures) {
      refreshSoon(Math.min(POLL_MS * 2 ** failures, POLL_MAX_MS));
      return;
    }

    // A box nobody has added to for a while is asked after less often. Anything arriving,
    // or the visitor turning back to the tab, puts it on the fast cadence again.
    const quiet = Date.now() - lastArrival > QUIET_AFTER_MS;
    // A little jitter, so a table full of phones doesn't ask all at the same instant.
    refreshSoon((quiet ? QUIET_POLL_MS : POLL_MS) + Math.random() * 2000);
  }

  async function refresh() {
    if (liveStopped || document.hidden) return;
    if (polling) {
      // An upload finished while a poll was already on its way out, so it can't know about it.
      askAgain = true;
      return;
    }

    polling = true;
    try {
      const res = await fetch(`/p/${poolCode}?handler=since&since=${cursor}`);
      if (res.status === 401 || res.status === 404) {
        // The box locked itself again, or is gone: nothing left worth asking for.
        liveStopped = true;
        return;
      }
      if (!res.ok) throw new Error("live refresh failed");
      addTiles(await res.text());
      failures = 0;
    } catch {
      failures = Math.min(failures + 1, 4);
    } finally {
      polling = false;
      if (askAgain) {
        askAgain = false;
        refreshSoon(400);
      } else {
        scheduleNext();
      }
    }
  }

  function addTiles(html) {
    const parsed = document.createElement("template");
    parsed.innerHTML = html;
    const incoming = [...parsed.content.querySelectorAll(".tile")];
    if (!incoming.length) return;

    // The feed re-offers the last moment's prints so none can slip between two polls;
    // whatever is already hanging here is dropped.
    for (const tile of incoming) {
      cursor = Math.max(cursor, Number(tile.dataset.uploaded) || 0);
    }
    const known = new Set([...gallery.children].map((tile) => tile.dataset.id));
    const fresh = incoming.filter((tile) => !known.has(tile.dataset.id));
    if (!fresh.length) return;
    lastArrival = Date.now();

    const who = filterSelect ? filterSelect.value : "";
    keepingPlace(() => {
      for (const tile of fresh) {
        if (who && tile.dataset.uploader !== who) {
          tile.style.display = "none";
        }
        insertInOrder(tile);
        bindAnimation(tile);
      }
      refreshChrome();
    });
  }

  // Prints hang by when they were taken, so one from earlier in the day goes back into its
  // place in the afternoon rather than landing at the end.
  function insertInOrder(tile) {
    for (const existing of gallery.children) {
      if (compareTiles(tile, existing) < 0) {
        gallery.insertBefore(tile, existing);
        return;
      }
    }
    gallery.appendChild(tile);
  }

  function compareTiles(a, b) {
    return (
      (Number(a.dataset.sort) || 0) - (Number(b.dataset.sort) || 0) ||
      (Number(a.dataset.uploaded) || 0) - (Number(b.dataset.uploaded) || 0) ||
      (a.dataset.id < b.dataset.id ? -1 : a.dataset.id > b.dataset.id ? 1 : 0)
    );
  }

  // Photos arriving must not shove the page around under whoever is reading it: measure a
  // print they can see, let the new ones in, then take back however far it moved.
  function keepingPlace(insert) {
    const measure = anchorMeasure();
    const before = measure();
    insert();
    const shift = measure() - before;
    if (shift && window.scrollY > 0) {
      window.scrollBy(0, shift);
    }
  }

  function anchorMeasure() {
    for (const tile of gallery.children) {
      if (tile.getBoundingClientRect().bottom > 0) {
        return () => tile.getBoundingClientRect().top;
      }
    }
    return () => gallery.getBoundingClientRect().bottom;
  }

  // Everything around the grid that counts prints, on arrivals and deletes alike.
  function refreshChrome() {
    const tiles = [...gallery.querySelectorAll(".tile")];
    const total = tiles.length;
    const mine = tiles.filter((tile) => tile.classList.contains("mine")).length;

    const counts = new Map();
    for (const tile of tiles) {
      counts.set(tile.dataset.uploader, (counts.get(tile.dataset.uploader) || 0) + 1);
    }

    emptyState.hidden = total > 0;
    photoCount.textContent = `${total} photo${total === 1 ? "" : "s"}`;
    peopleCount.hidden = counts.size === 0;
    peopleCount.textContent = `· from ${counts.size} ${counts.size === 1 ? "person" : "people"}`;

    show("download-all", total > 0);
    show("download-all-empty", total === 0);
    show("download-others", total > mine);
    show("download-others-empty", total <= mine);
    if (saveGalleryBtn) saveGalleryBtn.hidden = !canSaveToGallery || total === 0;

    refreshFilter(total, counts);
  }

  function show(id, shown) {
    document.getElementById(id).hidden = !shown;
  }

  function refreshFilter(total, counts) {
    if (!filterSelect) return;
    const names = [...counts.keys()].sort((a, b) => a.localeCompare(b));
    const signature = [total, ...names.map((name) => `${name}=${counts.get(name)}`)].join("\u0000");
    // Rebuilding this under an open dropdown would be exactly the interruption the live
    // refresh exists to avoid, so it is only touched when it really changed.
    if (filterSelect.dataset.signature === signature) return;
    filterSelect.dataset.signature = signature;

    const chosen = filterSelect.value;
    filterSelect.replaceChildren(makeOption("", `Everyone (${total})`));
    for (const name of names) {
      filterSelect.appendChild(makeOption(name, `${name} (${counts.get(name)})`));
    }

    filterSelect.value = chosen;
    if (filterSelect.selectedIndex < 0) {
      // The person being filtered on has no photos left: fall back to showing everyone.
      filterSelect.selectedIndex = 0;
      applyFilter();
    }
  }

  function makeOption(value, label) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    return option;
  }

  document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
      clearTimeout(pollTimer);
    } else {
      lastArrival = Date.now();
      refreshSoon(300);
    }
  });

  // Back on the network after a dropout, rather than sitting out the backoff.
  window.addEventListener("online", () => refreshSoon(300));

  refreshChrome();
  scheduleNext();

  function escapeHtml(s) {
    const div = document.createElement("div");
    div.textContent = s;
    return div.innerHTML;
  }
})();
