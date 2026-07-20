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
    let anyAdded = false;
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
          anyAdded = true;
        } else if (r && r.status === "duplicate") {
          status.textContent = "already in box";
          status.className = "status";
        } else {
          status.textContent = (r && r.reason) || "failed";
          status.className = "status fail";
        }
      } catch (err) {
        status.textContent = err.message;
        status.className = "status fail";
      }
    }
    if (anyAdded) {
      location.reload();
    }
  }

  function uploadOne(file, onProgress) {
    // XHR instead of fetch: fetch has no upload progress events.
    return new Promise((resolve, reject) => {
      const form = new FormData();
      form.append("uploaderName", nameInput.value.trim());
      form.append("files", file, file.name);

      const xhr = new XMLHttpRequest();
      xhr.open("POST", `/api/p/${poolCode}/photos`);
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

  filterSelect?.addEventListener("change", () => {
    const who = filterSelect.value;
    for (const tile of gallery.querySelectorAll(".tile")) {
      tile.style.display = !who || tile.dataset.uploader === who ? "" : "none";
    }
  });

  // ---------- Delete ----------

  gallery.addEventListener("click", async (e) => {
    const btn = e.target.closest(".delete-btn");
    if (!btn) return;
    e.stopPropagation();
    const tile = btn.closest(".tile");
    if (!confirm("Delete this photo?")) return;

    const res = await fetch(`/api/photos/${tile.dataset.id}`, { method: "DELETE" });
    if (res.ok) {
      tile.remove();
    } else {
      alert("Could not delete this photo.");
    }
  });

  // ---------- Lightbox ----------

  const lightbox = document.getElementById("lightbox");
  const lbImg = document.getElementById("lightbox-img");
  const lbCaption = document.getElementById("lightbox-caption");
  const lbDownload = document.getElementById("lightbox-download");
  let currentIndex = -1;

  function visibleTiles() {
    return [...gallery.querySelectorAll(".tile")].filter((t) => t.style.display !== "none");
  }

  function showAt(index) {
    const tiles = visibleTiles();
    if (!tiles.length) return;
    currentIndex = (index + tiles.length) % tiles.length;
    const tile = tiles[currentIndex];
    // The lightbox shows the web-safe "display" proxy: full-screen sharp but far smaller
    // than a 50MB phone original, and viewable in every browser (including HEIC). The
    // Download button always fetches the true original.
    const base = tile.dataset.original.replace(/\/original$/, "");
    lbImg.src = base + "/display";
    lbCaption.textContent = `${tile.dataset.uploader} · ${tile.dataset.filename}`;
    lbDownload.href = tile.dataset.original + "?download=true";
    lightbox.hidden = false;
    document.body.style.overflow = "hidden";
  }

  function closeLightbox() {
    lightbox.hidden = true;
    lbImg.src = "";
    document.body.style.overflow = "";
  }

  gallery.addEventListener("click", (e) => {
    const tile = e.target.closest(".tile");
    if (!tile || e.target.closest(".delete-btn")) return;
    showAt(visibleTiles().indexOf(tile));
  });

  lightbox.querySelector(".lb-close").addEventListener("click", closeLightbox);
  lightbox.querySelector(".lb-prev").addEventListener("click", () => showAt(currentIndex - 1));
  lightbox.querySelector(".lb-next").addEventListener("click", () => showAt(currentIndex + 1));
  lightbox.addEventListener("click", (e) => {
    if (e.target === lightbox) closeLightbox();
  });
  document.addEventListener("keydown", (e) => {
    if (lightbox.hidden) return;
    if (e.key === "Escape") closeLightbox();
    if (e.key === "ArrowLeft") showAt(currentIndex - 1);
    if (e.key === "ArrowRight") showAt(currentIndex + 1);
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
      showAt(currentIndex + (dx < 0 ? 1 : -1));
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

  function escapeHtml(s) {
    const div = document.createElement("div");
    div.textContent = s;
    return div.innerHTML;
  }
})();
