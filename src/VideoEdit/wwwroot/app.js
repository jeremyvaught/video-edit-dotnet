const state = { project: null, noteTimers: {} };

const $ = (id) => document.getElementById(id);

function ensure(id, tag = "div") {
  const existing = $(id);
  if (existing) {
    return existing;
  }

  const created = document.createElement(tag);
  created.id = id;
  const footer = document.querySelector("footer.actions");
  document.body.insertBefore(created, footer);
  return created;
}

function clear(id) {
  const el = $(id);
  if (el) {
    el.innerHTML = "";
  }
  return el;
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : {};
  if (!response.ok) {
    throw new Error(data.message || data.error || response.statusText);
  }
  return data;
}

function sceneHasBlockingEdits(scene) {
  return Boolean(scene.hasUnresolvedTiming) || scene.edits.some((edit) =>
    edit.decision === "pending" || edit.decision === "flagged" || !edit.decisionMatchesProposal);
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (ch) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;",
  })[ch]);
}

function rerecordItem(item) {
  const article = document.createElement("article");
  const tags = [];
  if (item.severity === "minor") {
    tags.push("minor");
  }
  if (item.status === "flagged") {
    tags.push("flagged");
  }
  article.className = item.status === "flagged"
    ? "rerecord-item flagged"
    : item.severity === "minor" ? "rerecord-item minor" : "rerecord-item";
  const timingActions = item.kind === "timing" && item.status === "pending"
    ? `<p class="actions">
        <button type="button" data-timing="${item.id}" data-timing-decision="extend">Extend picture</button>
        <button type="button" data-timing="${item.id}" data-timing-decision="trim">Trim pickup</button>
        <button type="button" data-timing="${item.id}" data-timing-decision="reject">Reject pickup</button>
      </p>`
    : "";
  const driftActions = item.kind !== "timing"
    ? `<p class="actions">
        ${item.kind === "substitution" ? `<button type="button" data-dismiss="${item.id}">Fine as spoken</button>` : ""}
        <button type="button" data-cut="${item.id}">Cut</button>
        <button type="button" data-pickup-open="${item.id}">Record pickup</button>
        <button type="button" data-generate="${item.id}">Generate (for cutaways / off-camera)</button>
        <button type="button" data-flag="${item.id}"${item.status === "flagged" ? " class=\"flagged\"" : ""}>${item.status === "flagged" ? "Flagged" : "Flag"}</button>
      </p>
      <div class="pickup-panel" data-pickup-panel="${item.id}" hidden>
        <p class="meta">Say this sentence</p>
        <blockquote>${escapeHtml(item.scriptSentence || item.scriptText || "")}</blockquote>
        <p class="meta">Drop or choose an audio or video file. It is copied to sources/pickups/ and prepared as a sentence replacement.</p>
        <input type="file" accept="audio/*,video/*" data-pickup-file="${item.id}">
        <p class="actions">
          <button type="button" data-pickup-send="${item.id}">Use this take</button>
        </p>
      </div>`
    : "";
  article.innerHTML = `
    <p class="kind">${item.kind}${tags.length ? ` · ${tags.join(" · ")}` : ""}</p>
    ${timeHeading(item.start, item.end, item.sourceStart, item.sourceEnd)}
    <p><span class="meta">Script</span> ${escapeHtml(item.scriptText) || "—"}</p>
    <p><span class="meta">Said</span> ${escapeHtml(item.spokenText) || "—"}</p>
    <p class="meta">${escapeHtml(item.reason) || ""}</p>
    ${item.noteNotUnderstood ? `<p class="request">Note not understood; re-propose manually.</p>` : ""}
    <a href="${item.contextUrl}">Source time</a>
    ${timingActions}
    ${driftActions}
    <textarea data-finding-note="${item.id}" placeholder="Note — saved as you type">${escapeHtml(item.note) || ""}</textarea>
  `;
  return article;
}

function timeHeading(start, end, sourceStart, sourceEnd) {
  const source = `${formatTime(sourceStart ?? start)}–${formatTime(sourceEnd ?? end)}`;
  const shown = Math.abs((end ?? start) - start) < 0.001
    ? formatTime(start)
    : `${formatTime(start)}–${formatTime(end)}`;
  return `<h3 class="output-time" title="source ${source}">${shown}</h3>`;
}

function formatTime(seconds) {
  const whole = Math.max(0, seconds);
  const m = Math.floor(whole / 60);
  const s = (whole % 60).toFixed(2).padStart(5, "0");
  return `${m}:${s}`;
}

function applyActionEligibility(project) {
  const busy = Boolean(project?.status?.busy);
  const approveBlocked = !project || !project.scenes.every((scene) => scene.accepted);
  const exportBlocked = !project || !project.canExport;
  document.querySelectorAll("button").forEach((button) => {
    if (busy) {
      button.disabled = true;
      return;
    }
    if (button.dataset.sceneDecision === "approved") {
      const scene = project?.scenes.find((item) => item.id === button.dataset.scene);
      button.disabled = !scene || scene.accepted || sceneHasBlockingEdits(scene);
      return;
    }
    if (button.id === "approve-final") {
      button.disabled = approveBlocked;
      return;
    }
    if (button.id === "export") {
      button.disabled = exportBlocked;
      return;
    }
    button.disabled = false;
  });
}

function setBusy(on, label) {
  $("busy").hidden = !on;
  $("busy-label").textContent = label || "Media operation in progress";
  if (on) {
    document.querySelectorAll("button").forEach((button) => {
      button.disabled = true;
    });
    return;
  }
  applyActionEligibility(state.project);
}

function render(project) {
  state.project = project;
  $("title").textContent = project.name;
  $("meta").textContent =
    `rev ${project.revision} · source ${formatTime(project.sourceDurationSeconds)} · ` +
    `output ${formatTime(project.outputDurationSeconds)} · ${project.compositionHash.slice(0, 12)}`;
  $("status").textContent = project.status?.busy
    ? `Busy: ${project.status.operation}`
    : "Idle";

  $("rough-stale").hidden = !project.roughCutStale;
  const rough = $("rough-player");
  if (project.roughCutUrl) {
    if (rough.dataset.src !== project.roughCutUrl) {
      rough.src = project.roughCutUrl;
      rough.dataset.src = project.roughCutUrl;
    }
  } else {
    rough.removeAttribute("src");
    delete rough.dataset.src;
  }

  const questions = $("questions");
  const list = clear("question-list");
  if (project.questions?.length && questions && list) {
    questions.hidden = false;
    for (const question of project.questions) {
      const item = document.createElement("li");
      item.textContent = question.around == null
        ? question.text
        : `${formatTime(question.around)} — ${question.text}`;
      list.append(item);
    }
  } else if (questions) {
    questions.hidden = true;
  }

  const rerecord = $("rerecord");
  const rerecordList = clear("rerecord-list");
  const minorBox = $("rerecord-minor");
  const minorList = clear("rerecord-minor-list");
  clear("rerecord-resolved-list");
  const items = project.rerecordList || [];
  const major = items.filter((item) => item.severity !== "minor");
  const minor = items.filter((item) => item.severity === "minor");
  if (items.length && rerecord && rerecordList) {
    rerecord.hidden = false;
    for (const item of major) {
      rerecordList.append(rerecordItem(item));
    }
    if (minorBox) {
      minorBox.hidden = minor.length === 0;
    }
    if (minor.length && minorList) {
      const summary = $("rerecord-minor-summary");
      if (summary) {
        summary.textContent = `${minor.length} minor (contractions and number agreement)`;
      }
      for (const item of minor) {
        minorList.append(rerecordItem(item));
      }
    }
  } else if (rerecord) {
    rerecord.hidden = true;
    if (minorBox) {
      minorBox.hidden = true;
    }
  }

  const scenes = ensure("scenes", "section");
  scenes.innerHTML = "";
  for (const [index, scene] of (project.scenes || []).entries()) {
    const section = document.createElement("section");
    const previous = project.scenes[index - 1];
    const next = project.scenes[index + 1];
    const transition = [
      previous ? `Hard cut from ${previous.title}` : "Opens the sequence",
      next ? `hard cut to ${next.title}` : "closes the sequence",
    ].join(" · ");
    const review = scene.reviewDecision || (scene.accepted ? "approved" : "pending");
    section.className = scene.kind === "generated" ? "scene generated-scene" : "scene";
    section.innerHTML = `
      <div class="panel-head">
        <div>
          <h2>${scene.title}</h2>
          <p class="meta">${scene.kind} · ${formatTime(scene.durationSeconds)} · ${review}</p>
          <p class="meta">${transition}</p>
          ${scene.selections?.length ? `<p class="meta">${scene.selections.map((item) =>
            `<a href="${item.contextUrl}" title="source ${formatTime(item.sourceStart ?? item.start)}–${formatTime(item.sourceEnd ?? item.end)}">${item.sourceAssetId} ${formatTime(item.start)}–${formatTime(item.end)}${
              item.audioSourceAssetId
                ? ` · audio ${item.audioSourceAssetId} ${formatTime(item.audioStart)}–${formatTime(item.audioEnd)}${item.needsCutaway ? " · needs cutaway" : ""}`
                : ""
            }</a>`
          ).join(" · ")}</p>` : ""}
        </div>
        <div class="decisions">
          <button type="button" data-scene="${scene.id}" data-scene-decision="approved" class="${review === "approved" ? "approved" : ""}">Accept</button>
          <button type="button" data-scene="${scene.id}" data-scene-decision="flagged" class="${review === "flagged" ? "flagged" : ""}">Flag</button>
          <button type="button" data-scene="${scene.id}" data-scene-decision="rejected" class="${review === "rejected" ? "rejected" : ""}">Reject</button>
        </div>
      </div>
      <textarea data-scene-note="${scene.id}" placeholder="Scene note — sent with Accept, Flag, or Reject">${scene.note || ""}</textarea>
      ${scene.kind === "generated" ? `
        <article class="generated">
          <p class="kind">Still + generated narration</p>
          <p class="removed">${scene.scriptText || "(no script)"}</p>
          <p class="meta">voice ${scene.voiceId || "unset"} · ${scene.modelId || "unset"} · stability ${scene.stability ?? "—"} · similarity ${scene.similarityBoost ?? "—"} · ${formatTime(scene.durationSeconds)}</p>
          <div class="generated-visuals">
            <div class="clip">
              <p class="kind">Still image</p>
              ${scene.stillUrl ? `<img src="${scene.stillUrl}" alt="Generated scene still">` : "<p class=\"meta\">No still image.</p>"}
            </div>
            <div class="clip">
              <p class="kind">Playable scene</p>
              ${scene.previewStale ? '<p class="stale">Scene preview is missing or stale. Rebuild the rough cut.</p>' : ""}
              ${scene.previewUrl ? `<video controls playsinline src="${scene.previewUrl}"></video>` : "<p class=\"meta\">No scene preview yet.</p>"}
            </div>
          </div>
        </article>
      ` : ""}
    `;
    for (const edit of scene.edits) {
      const article = document.createElement("article");
      article.className = "edit";
      article.innerHTML = `
        <div>
          <p class="kind">${edit.kind}</p>
          ${timeHeading(edit.start, edit.end, edit.sourceStart, edit.sourceEnd)}
          <p class="removed">${edit.removedText || "(no transcript)"}</p>
          <p>${edit.reason}</p>
          ${edit.noteNotUnderstood ? `<p class="request">Note not understood; re-propose manually.</p>` : edit.reproposedFromNote ? `<p class="request">Re-proposed from your note · proposal rev ${edit.proposalRevision}</p>` : ""}
          <p class="meta">${edit.decision} · confidence ${edit.confidence}${edit.decisionMatchesProposal ? "" : " · stale proposal decision"}${edit.noCleanBoundary ? " · no clean silence boundary" : ""}</p>
          <div class="decisions">
            <button type="button" data-edit="${edit.id}" data-decision="approved" class="${edit.decision === "approved" ? "approved" : ""}">Approve</button>
            <button type="button" data-edit="${edit.id}" data-decision="rejected" class="${edit.decision === "rejected" ? "rejected" : ""}">Reject</button>
            <button type="button" data-edit="${edit.id}" data-decision="flagged" class="${edit.decision === "flagged" ? "flagged" : ""}">Flag</button>
            <a href="${edit.contextUrl}">Longer context</a>
          </div>
          <textarea data-note="${edit.id}" placeholder="Note — saved as you type">${edit.note || ""}</textarea>
        </div>
        <div class="clips">
          ${edit.spliceStale ? '<p class="stale">Previews are missing or stale. Rebuild splice previews.</p>' : ""}
          <div class="clip">
            <p class="kind">Before</p>
            ${edit.originalUrl ? `<video controls playsinline src="${edit.originalUrl}"></video>` : "<p class=\"meta\">No original clip yet.</p>"}
          </div>
          <div class="clip">
            <p class="kind">After the cut</p>
            ${edit.spliceUrl ? `<video controls playsinline src="${edit.spliceUrl}"></video>` : "<p class=\"meta\">No splice yet.</p>"}
          </div>
        </div>
      `;
      section.append(article);
    }
    scenes.append(section);
  }

  $("blocks").textContent = project.canExport
    ? "Ready to export the approved sequence."
    : (project.blocks || []).join(" ");
  applyActionEligibility(project);
}

async function refresh() {
  const project = await api("/api/project");
  try {
    render(project);
  } catch (error) {
    const blocks = $("blocks");
    if (blocks) {
      blocks.textContent = error.message;
    }
  }
}

function editById(editId) {
  return state.project?.scenes.flatMap((scene) => scene.edits).find((edit) => edit.id === editId);
}

function applyProject(project) {
  const drafts = {};
  let focusedId = null;
  let selection = null;
  const active = document.activeElement;
  if (active?.dataset?.note || active?.dataset?.findingNote) {
    focusedId = active.dataset.note ? `edit:${active.dataset.note}` : `finding:${active.dataset.findingNote}`;
    selection = [active.selectionStart, active.selectionEnd];
  }
  document.querySelectorAll("textarea[data-note]").forEach((el) => {
    drafts[`edit:${el.dataset.note}`] = el.value;
  });
  document.querySelectorAll("textarea[data-finding-note]").forEach((el) => {
    drafts[`finding:${el.dataset.findingNote}`] = el.value;
  });
  render(project);
  for (const [id, value] of Object.entries(drafts)) {
    const [kind, key] = id.split(":");
    const el = kind === "finding"
      ? document.querySelector(`[data-finding-note="${key}"]`)
      : document.querySelector(`[data-note="${key}"]`);
    const current = kind === "finding"
      ? (state.project?.rerecordList || []).find((item) => item.id === key)?.note || ""
      : editById(key)?.note || "";
    if (el && value !== current) {
      el.value = value;
    }
  }
  if (focusedId) {
    const [kind, key] = focusedId.split(":");
    const el = kind === "finding"
      ? document.querySelector(`[data-finding-note="${key}"]`)
      : document.querySelector(`[data-note="${key}"]`);
    if (el) {
      el.focus();
      if (selection) {
        el.setSelectionRange(selection[0], selection[1]);
      }
    }
  }
}

async function saveNote(editId, value) {
  const current = editById(editId);
  if (!current || !state.project || (current.note || "") === value) {
    return;
  }
  state.project = await api(`/api/edits/${editId}/note`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
      note: value,
    }),
  });
}

async function saveFindingNote(findingId, value) {
  const current = (state.project?.rerecordList || []).find((item) => item.id === findingId);
  if (!current || !state.project || (current.note || "") === value) {
    return;
  }
  applyProject(await api(`/api/findings/${findingId}/note`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
      note: value,
    }),
  }));
}

function queueNoteSave(editId, value) {
  clearTimeout(state.noteTimers[editId]);
  state.noteTimers[editId] = setTimeout(() => {
    saveNote(editId, value).catch((error) => {
      $("blocks").textContent = error.message;
    });
  }, 300);
}

async function decideScene(sceneId, decision) {
  const note = document.querySelector(`[data-scene-note="${sceneId}"]`)?.value || "";
  applyProject(await api(`/api/scenes/${sceneId}/decision`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
      decision,
      note,
    }),
  }));
}

async function decideTiming(findingId, decision) {
  applyProject(await api(`/api/findings/${findingId}/timing`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
      decision,
    }),
  }));
}

async function dismissFinding(findingId) {
  applyProject(await api(`/api/findings/${findingId}/dismiss`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
    }),
  }));
}

async function cutFinding(findingId) {
  setBusy(true, "Proposing clause cut");
  try {
    applyProject(await api(`/api/findings/${findingId}/cut`, {
      method: "POST",
      body: JSON.stringify({
        expectedRevision: state.project.revision,
      }),
    }));
  } finally {
    setBusy(false);
  }
}

async function flagFinding(findingId) {
  applyProject(await api(`/api/findings/${findingId}/flag`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
    }),
  }));
}

function pickupFileFor(findingId) {
  return document.querySelector(`[data-pickup-file="${findingId}"]`)?.files?.[0] || null;
}

async function uploadPickup(findingId, file) {
  if (!file) {
    throw new Error("Choose or drop an audio or video file first.");
  }
  const body = new FormData();
  body.append("file", file);
  body.append("expectedRevision", String(state.project.revision));
  setBusy(true, "Preparing pickup");
  try {
    const response = await fetch(`/api/findings/${findingId}/pickup`, {
      method: "POST",
      body,
    });
    const text = await response.text();
    const data = text ? JSON.parse(text) : {};
    if (!response.ok) {
      throw new Error(data.message || data.error || response.statusText);
    }
    applyProject(data);
  } finally {
    setBusy(false);
  }
}

async function generatePickup(findingId) {
  const item = (state.project?.rerecordList || []).find((entry) => entry.id === findingId);
  const sentence = item?.scriptSentence || item?.scriptText || "";
  if (!window.confirm(
    `This calls ElevenLabs (paid) to speak:\n\n${sentence}\n\nUse this for cutaways / off-camera.`)) {
    return;
  }
  setBusy(true, "Generating off-camera pickup");
  try {
    applyProject(await api(`/api/findings/${findingId}/generate`, {
      method: "POST",
      body: JSON.stringify({
        expectedRevision: state.project.revision,
      }),
    }));
  } finally {
    setBusy(false);
  }
}

async function decide(editId, decision) {
  const note = document.querySelector(`[data-note="${editId}"]`)?.value || "";
  applyProject(await api(`/api/edits/${editId}/decision`, {
    method: "POST",
    body: JSON.stringify({
      expectedRevision: state.project.revision,
      decision,
      note,
    }),
  }));
}

async function run(action, sceneId) {
  try {
    setBusy(true, action);
    if (action === "splices") {
      render(await api("/api/previews/splices", { method: "POST", body: "{}" }));
    } else if (action === "rough") {
      render(await api("/api/previews/rough", { method: "POST", body: "{}" }));
    } else if (action === "approve") {
      render(await api("/api/approve-final", {
        method: "POST",
        body: JSON.stringify({ expectedRevision: state.project.revision }),
      }));
    } else if (action === "export") {
      const result = await api("/api/export", { method: "POST", body: "{}" });
      $("blocks").textContent = `Exported ${result.fileName}`;
      await refresh();
    }
  } catch (error) {
    $("blocks").textContent = error.message;
  } finally {
    setBusy(false);
  }
}

document.addEventListener("input", (event) => {
  const note = event.target.closest("textarea[data-note]");
  if (note) {
    queueNoteSave(note.dataset.note, note.value);
    return;
  }
  const finding = event.target.closest("textarea[data-finding-note]");
  if (finding) {
    queueFindingNoteSave(finding.dataset.findingNote, finding.value);
  }
});

document.addEventListener("focusout", (event) => {
  const note = event.target.closest("textarea[data-note]");
  if (note) {
    clearTimeout(state.noteTimers[note.dataset.note]);
    saveNote(note.dataset.note, note.value).catch((error) => {
      $("blocks").textContent = error.message;
    });
    return;
  }
  const finding = event.target.closest("textarea[data-finding-note]");
  if (!finding) {
    return;
  }
  clearTimeout(state.noteTimers[`finding:${finding.dataset.findingNote}`]);
  saveFindingNote(finding.dataset.findingNote, finding.value).catch((error) => {
    $("blocks").textContent = error.message;
  });
});

function queueFindingNoteSave(findingId, value) {
  const key = `finding:${findingId}`;
  clearTimeout(state.noteTimers[key]);
  state.noteTimers[key] = setTimeout(() => {
    saveFindingNote(findingId, value).catch((error) => {
      $("blocks").textContent = error.message;
    });
  }, 300);
}

document.addEventListener("dragover", (event) => {
  const panel = event.target.closest("[data-pickup-panel]");
  if (!panel) {
    return;
  }
  event.preventDefault();
  panel.classList.add("drop-over");
});

document.addEventListener("dragleave", (event) => {
  const panel = event.target.closest("[data-pickup-panel]");
  if (panel) {
    panel.classList.remove("drop-over");
  }
});

document.addEventListener("drop", (event) => {
  const panel = event.target.closest("[data-pickup-panel]");
  if (!panel) {
    return;
  }
  event.preventDefault();
  panel.classList.remove("drop-over");
  const file = event.dataTransfer?.files?.[0];
  const input = panel.querySelector("[data-pickup-file]");
  if (file && input) {
    const transfer = new DataTransfer();
    transfer.items.add(file);
    input.files = transfer.files;
  }
});

document.addEventListener("click", (event) => {
  const button = event.target.closest("button");
  if (!button) {
    return;
  }
  if (button.dataset.dismiss) {
    dismissFinding(button.dataset.dismiss).catch((error) => {
      $("blocks").textContent = error.message;
    });
    return;
  }
  if (button.dataset.cut) {
    cutFinding(button.dataset.cut).catch((error) => {
      $("blocks").textContent = error.message;
    });
    return;
  }
  if (button.dataset.flag) {
    flagFinding(button.dataset.flag).catch((error) => {
      $("blocks").textContent = error.message;
    });
    return;
  }
  if (button.dataset.pickupOpen) {
    const panel = document.querySelector(`[data-pickup-panel="${button.dataset.pickupOpen}"]`);
    if (panel) {
      panel.hidden = !panel.hidden;
    }
    return;
  }
  if (button.dataset.pickupSend) {
    uploadPickup(button.dataset.pickupSend, pickupFileFor(button.dataset.pickupSend)).catch((error) => {
      $("blocks").textContent = error.message;
    });
    return;
  }
  if (button.dataset.generate) {
    generatePickup(button.dataset.generate).catch((error) => {
      $("blocks").textContent = error.message;
    });
    return;
  }
  if (button.dataset.timingDecision) {
    decideTiming(button.dataset.timing, button.dataset.timingDecision);
    return;
  }
  if (button.dataset.decision) {
    decide(button.dataset.edit, button.dataset.decision);
    return;
  }
  if (button.dataset.sceneDecision) {
    decideScene(button.dataset.scene, button.dataset.sceneDecision);
    return;
  }
  if (button.dataset.action) {
    run(button.dataset.action, button.dataset.scene);
  }
});

refresh().catch((error) => {
  $("title").textContent = "Could not load project";
  $("blocks").textContent = error.message;
});
