(() => {
  const mdOut = document.getElementById("md-out");
  const rendered = document.getElementById("rendered");
  const overlay = document.getElementById("drop-overlay");
  const status = document.getElementById("status-bar");
  const errorBanner = document.getElementById("error-banner");
  const copyBtn = document.getElementById("copy-btn");
  const downloadBtn = document.getElementById("download-btn");
  const pickBtn = document.getElementById("pick-btn");
  const fileInput = document.getElementById("file-input");

  let currentMarkdown = "";
  let currentName = "";
  let overlayVisible = false;

  function showOverlay() {
    if (overlayVisible) return;
    overlayVisible = true;
    overlay.hidden = false;
  }
  function hideOverlay() {
    overlayVisible = false;
    overlay.hidden = true;
  }

  pickBtn.addEventListener("click", () => fileInput.click());
  fileInput.addEventListener("change", (e) => {
    if (e.target.files && e.target.files[0]) {
      handleFile(e.target.files[0]);
      fileInput.value = "";
    }
  });

  // dragover fires continuously while a drag is over the window. Use it both to
  // call preventDefault (required so the browser allows a drop) and to show the
  // overlay. dragenter/dragleave on nested elements are noisy and order-dependent
  // across browsers; dragover + relatedTarget=null on dragleave is the canonical
  // "drag actually left the window" signal.
  window.addEventListener("dragover", (e) => {
    if (!e.dataTransfer || !Array.from(e.dataTransfer.types).includes("Files")) return;
    e.preventDefault();
    showOverlay();
  });
  window.addEventListener("dragleave", (e) => {
    if (e.relatedTarget === null) hideOverlay();
  });
  window.addEventListener("drop", (e) => {
    e.preventDefault();
    hideOverlay();
    const file = e.dataTransfer?.files?.[0];
    if (file) handleFile(file);
  });
  // Belt and suspenders: if the user hits Esc, kill the overlay.
  window.addEventListener("keydown", (e) => { if (e.key === "Escape") hideOverlay(); });

  copyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(currentMarkdown);
      setStatus("Copied to clipboard.", "ok");
    } catch {
      setStatus("Copy failed (clipboard permissions?).", "err");
    }
  });

  downloadBtn.addEventListener("click", () => {
    const blob = new Blob([currentMarkdown], { type: "text/markdown;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = (currentName.replace(/\.[^.]+$/, "") || "document") + ".md";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(a.href);
  });

  async function handleFile(file) {
    hideOverlay();
    hideError();
    currentName = file.name;
    setStatus(`Converting ${file.name} (${formatBytes(file.size)})…`, "busy");
    mdOut.value = "";
    rendered.innerHTML = '<p class="placeholder">Working…</p>';
    copyBtn.disabled = true;
    downloadBtn.disabled = true;

    const fd = new FormData();
    fd.append("file", file, file.name);

    let resp;
    try {
      resp = await fetch("/api/v1/convert", { method: "POST", body: fd });
    } catch (err) {
      return showError(`Network error: ${err.message}`);
    }

    if (!resp.ok) {
      let detail = "";
      try {
        const body = await resp.json();
        detail = body.detail || body.error || JSON.stringify(body);
      } catch {
        detail = await resp.text();
      }
      return showError(`HTTP ${resp.status}: ${detail}`);
    }

    const body = await resp.json();
    currentMarkdown = body.markdown || "";
    mdOut.value = currentMarkdown;
    rendered.innerHTML = window.marked
      ? window.marked.parse(currentMarkdown)
      : escapeHtml(currentMarkdown);
    copyBtn.disabled = !currentMarkdown;
    downloadBtn.disabled = !currentMarkdown;
    setStatus(
      `Converted ${body.filename} • ${formatBytes(body.bytes)} • ${body.duration_ms} ms`,
      "ok"
    );
  }

  function setStatus(msg, kind) {
    status.textContent = msg;
    status.className = kind || "";
  }
  function showError(msg) {
    errorBanner.textContent = msg;
    errorBanner.hidden = false;
    setStatus("Failed.", "err");
    setTimeout(hideError, 6000);
  }
  function hideError() { errorBanner.hidden = true; }

  function formatBytes(n) {
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / 1024 / 1024).toFixed(2)} MB`;
  }
  function escapeHtml(s) {
    return s
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;");
  }
})();
