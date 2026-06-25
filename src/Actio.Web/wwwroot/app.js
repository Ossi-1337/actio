const pollActiveMilliseconds = 2000;
const pollIdleMilliseconds = 5000;
const pollHiddenMilliseconds = 15000;
const previewLineCount = 10;
const themeStorageKey = "actio-theme";

const state = {
  workflows: [],
  runs: [],
  selectedRun: null,
  selectedRunId: location.pathname.startsWith("/runs/") ? decodeURIComponent(location.pathname.split("/").pop()) : null,
  currentView: location.pathname.startsWith("/settings") ? "settings" : "runs",
  filter: "",
  projectRoot: "",
  health: null,
  cache: { cacheRoot: "", entries: [] },
  cacheMessage: "",
  openLogs: new Set(),
  logContents: new Map(),
  workflowFiles: new Map(),
  workflowMessages: new Map(),
  expandedWorkflowFiles: new Set(),
  refreshTimer: null,
  detailRequestId: 0,
  theme: loadTheme()
};

const el = {
  workflows: document.querySelector("#workflow-list"),
  runs: document.querySelector("#run-list"),
  detail: document.querySelector("#detail"),
  title: document.querySelector("#page-title"),
  projectRoot: document.querySelector("#project-root"),
  runCount: document.querySelector("#run-count"),
  filter: document.querySelector("#run-filter"),
  filters: document.querySelector(".filters"),
  themeToggle: document.querySelector("#theme-toggle"),
  navLinks: document.querySelectorAll("[data-view-link]"),
  runsView: document.querySelector("#runs-view"),
  settingsView: document.querySelector("#settings-view")
};

applyTheme(state.theme);

el.filter.addEventListener("input", () => {
  state.filter = el.filter.value.trim().toLowerCase();
  renderRuns();
});

el.themeToggle.addEventListener("click", event => {
  const button = event.target.closest("[data-theme-choice]");
  if (button) {
    setTheme(button.dataset.themeChoice);
  }
});

el.navLinks.forEach(link => {
  link.addEventListener("click", async event => {
    event.preventDefault();
    const view = link.dataset.viewLink === "settings" ? "settings" : "runs";
    state.currentView = view;
    history.pushState(null, "", view === "settings" ? "/settings" : selectedRunPath());
    await render();
  });
});

document.addEventListener("click", async event => {
  const button = event.target.closest("[data-workflow-action]");
  if (!button) {
    return;
  }

  const runId = button.dataset.runId;
  if (!runId) {
    return;
  }

  const action = button.dataset.workflowAction;
  if (action === "expand") {
    state.expandedWorkflowFiles.add(runId);
    updateWorkflowFileShell(runId);
  } else if (action === "collapse") {
    state.expandedWorkflowFiles.delete(runId);
    updateWorkflowFileShell(runId);
  } else if (action === "copy") {
    await copyWorkflowFile(runId);
  } else if (action === "download") {
    downloadWorkflowFile(runId);
  } else if (action === "edit") {
    await openWorkflowEditor(runId);
  }
});

window.addEventListener("popstate", () => {
  state.currentView = location.pathname.startsWith("/settings") ? "settings" : "runs";
  state.selectedRunId = location.pathname.startsWith("/runs/") ? decodeURIComponent(location.pathname.split("/").pop()) : null;
  render();
});

document.addEventListener("visibilitychange", () => {
  scheduleRefresh(document.hidden ? pollHiddenMilliseconds : 0);
});

load();

async function load() {
  try {
    await refreshData({ selectLatestRun: true });
  } catch {
    el.detail.innerHTML = `<section class="summary"><div class="empty">Actio web data is not available.</div></section>`;
  } finally {
    scheduleRefresh();
  }
}

async function refreshData(options = {}) {
  const [health, workflows, runs, cache] = await Promise.all([
    fetchJson("/api/health"),
    fetchJson("/api/workflows"),
    fetchJson("/api/runs"),
    fetchJson("/api/cache")
  ]);

  state.health = health;
  state.projectRoot = health.projectRoot;
  state.workflows = workflows;
  state.runs = runs;
  state.cache = cache;
  el.projectRoot.textContent = state.projectRoot;

  if (options.selectLatestRun && state.currentView === "runs" && !state.selectedRunId && runs.length > 0) {
    selectRun(runs[0].runId, { replaceHistory: true, renderNow: false });
  }

  await render();
}

async function render() {
  renderNavigation();

  if (state.currentView === "settings") {
    renderSettings();
    return;
  }

  renderRunsView();
  await renderDetail();
}

function renderNavigation() {
  el.navLinks.forEach(link => {
    const active = link.dataset.viewLink === state.currentView;
    link.classList.toggle("active", active);
    link.setAttribute("aria-current", active ? "page" : "false");
  });

  el.runsView.hidden = state.currentView !== "runs";
  el.settingsView.hidden = state.currentView !== "settings";
  el.filters.hidden = state.currentView !== "runs";
  el.title.textContent = state.currentView === "settings" ? "Settings" : "Workflow runs";
}

function renderRunsView() {
  renderWorkflows();
  renderRuns();
}

function renderWorkflows() {
  if (state.workflows.length === 0) {
    el.workflows.innerHTML = `<div class="empty">No workflows found.</div>`;
    return;
  }

  el.workflows.innerHTML = state.workflows.map(workflow => `
    <a class="workflow-item" href="#" data-workflow="${escapeHtml(workflow.fileName)}">
      <span>${escapeHtml(workflow.name)}</span>
      <span class="workflow-meta">${escapeHtml(workflow.fileName)} · ${workflow.runCount} runs</span>
    </a>
  `).join("");

  el.workflows.querySelectorAll("[data-workflow]").forEach(item => {
    item.addEventListener("click", event => {
      event.preventDefault();
      state.currentView = "runs";
      state.filter = item.dataset.workflow.toLowerCase();
      el.filter.value = item.dataset.workflow;
      history.pushState(null, "", selectedRunPath());
      render();
    });
  });
}

function renderRuns() {
  const runs = filteredRuns();
  el.runCount.textContent = `${runs.length} shown`;

  if (runs.length === 0) {
    el.runs.innerHTML = `<div class="empty">No matching runs.</div>`;
    return;
  }

  el.runs.innerHTML = runs.map(run => `
    <a class="run-row ${run.runId === state.selectedRunId ? "active" : ""}" href="/runs/${encodeURIComponent(run.runId)}" data-run="${escapeHtml(run.runId)}">
      <span class="status-dot ${statusClass(run.status)}"></span>
      <span>
        <span class="run-title">${escapeHtml(run.workflowName)}</span>
        <span class="run-sub muted">${escapeHtml(shortRunId(run.runId))} · ${escapeHtml(run.trigger)} · ${run.jobCount} jobs · ${run.artifactCount} artifacts</span>
      </span>
      <span class="run-time">${formatDate(run.startedAt)}<br>${formatDuration(run.durationMilliseconds)}</span>
    </a>
  `).join("");

  el.runs.querySelectorAll("[data-run]").forEach(item => {
    item.addEventListener("click", async event => {
      event.preventDefault();
      await selectRun(item.dataset.run);
    });
  });
}

async function renderDetail() {
  const requestId = ++state.detailRequestId;

  if (!state.selectedRunId) {
    state.selectedRun = null;
    el.detail.innerHTML = `<section class="summary"><div class="empty">No run selected.</div></section>`;
    return;
  }

  let run;
  try {
    run = await fetchJson(`/api/runs/${encodeURIComponent(state.selectedRunId)}`);
  } catch {
    if (requestId !== state.detailRequestId) {
      return;
    }

    state.selectedRun = null;
    el.title.textContent = "Workflow runs";
    el.detail.innerHTML = `<section class="summary"><div class="empty">Run is not available yet.</div></section>`;
    return;
  }

  if (requestId !== state.detailRequestId) {
    return;
  }

  state.selectedRun = run;
  el.title.textContent = run.workflowName;
  el.detail.innerHTML = [
    renderSummary(run),
    renderGraph(run),
    renderArtifacts(run),
    renderOutputs(run),
    renderJobs(run),
    renderWorkflowFileShell(run)
  ].join("");

  wireLogButtons(run);
  await loadWorkflowFile(run.runId);
  await refreshOpenLogs(run);
}

function renderSummary(run) {
  return `
    <section class="summary">
      <div class="summary-head">
        <h2>${escapeHtml(run.workflowName)}</h2>
        <span class="pill ${statusClass(run.status)}">${escapeHtml(run.status)}</span>
      </div>
      <div class="summary-grid">
        ${summaryCell("Started", formatDate(run.startedAt))}
        ${summaryCell("Duration", formatDuration(run.durationMilliseconds))}
        ${summaryCell("Trigger", "CLI")}
        ${run.triggers?.length ? summaryCell("Configured triggers", formatTriggers(run.triggers)) : ""}
        ${summaryCell("Workflow file", run.workflowPath ?? "Unknown")}
      </div>
    </section>
  `;
}

function renderGraph(run) {
  if (run.jobs.length === 0) {
    return `<section class="summary"><div class="empty">No jobs recorded yet.</div></section>`;
  }

  return `
    <section class="summary">
      <div class="summary-head"><h2>Job graph</h2></div>
      <div class="graph">
        ${run.jobs.map((job, index) => `
          ${index === 0 ? "" : `<span class="connector"></span>`}
          <div class="job-node">
            <div class="job-name">${escapeHtml(job.name)}</div>
            <div class="muted">${escapeHtml(job.status)} · ${job.steps.length} steps</div>
            <div class="muted">${job.needs.length ? `needs ${escapeHtml(job.needs.join(", "))}` : "no dependencies"}</div>
          </div>
        `).join("")}
      </div>
    </section>
  `;
}

function renderArtifacts(run) {
  if (run.artifacts.length === 0) {
    return "";
  }

  return `
    <section class="summary">
      <div class="summary-head"><h2>Artifacts</h2></div>
      <div class="link-list">
        ${run.artifacts.map(artifact => `
          <a class="pill" href="/api/runs/${encodeURIComponent(run.runId)}/artifacts?job=${encodeURIComponent(artifact.jobName)}&name=${encodeURIComponent(artifact.name)}" target="_blank" rel="noreferrer">
            ${escapeHtml(artifact.jobName)} / ${escapeHtml(artifact.name)}
          </a>
        `).join("")}
      </div>
    </section>
  `;
}

function renderOutputs(run) {
  if (run.outputs.length === 0) {
    return "";
  }

  return `
    <section class="summary">
      <div class="summary-head"><h2>Outputs</h2></div>
      <div class="link-list">
        ${run.outputs.map(output => `
          <span class="pill">${escapeHtml(output.jobName)}.${escapeHtml(output.name)}=${escapeHtml(output.value)}</span>
        `).join("")}
      </div>
    </section>
  `;
}

function renderJobs(run) {
  return run.jobs.map(job => `
    <section class="job-section">
      <div class="job-head">
        <h2>${escapeHtml(job.name)}</h2>
        <span class="pill ${statusClass(job.status)}">${escapeHtml(job.status)}</span>
      </div>
      <div class="job-body">
        ${job.errors.length ? `<div class="empty">${job.errors.map(escapeHtml).join("<br>")}</div>` : ""}
        ${job.steps.map(step => renderStep(run, job, step)).join("")}
      </div>
    </section>
  `).join("");
}

function renderStep(run, job, step) {
  const key = logKey(run.runId, job.name, step.name);
  const isOpen = state.openLogs.has(key);
  const content = state.logContents.get(key) ?? "Loading...";

  return `
    <div class="step-row">
      <span class="status-dot ${statusClass(step.status)}"></span>
      <span>
        <span class="job-name">${escapeHtml(step.name)}</span>
        <span class="run-sub muted">${escapeHtml(step.status)} · ${formatDuration(step.durationMilliseconds)}</span>
      </span>
      ${step.logPath ? `<button class="log-button" data-log-key="${escapeHtml(key)}" data-job="${escapeHtml(job.name)}" data-step="${escapeHtml(step.name)}">Log</button>` : `<span class="muted">No log</span>`}
    </div>
    <pre class="log-view" ${isOpen ? "" : "hidden"} data-log-key="${escapeHtml(key)}" data-job="${escapeHtml(job.name)}" data-step="${escapeHtml(step.name)}">${escapeHtml(content)}</pre>
  `;
}

function renderWorkflowFileShell(run) {
  return `
    <section class="workflow-file" data-workflow-file-run="${escapeHtml(run.runId)}">
      <div class="summary-head">
        <h2>Workflow file</h2>
        <div class="toolbar">
          <button class="icon-button" type="button" title="Copy workflow file" aria-label="Copy workflow file" data-workflow-action="copy" data-run-id="${escapeHtml(run.runId)}">${copyIcon()}</button>
          <button class="icon-button" type="button" title="Download workflow file" aria-label="Download workflow file" data-workflow-action="download" data-run-id="${escapeHtml(run.runId)}">${downloadIcon()}</button>
          <button class="text-button" type="button" data-workflow-action="edit" data-run-id="${escapeHtml(run.runId)}">Edit</button>
        </div>
      </div>
      <div class="workflow-file-body">${renderWorkflowFileBody(run.runId)}</div>
    </section>
  `;
}

function renderWorkflowFileBody(runId) {
  const content = state.workflowFiles.get(runId);
  const message = state.workflowMessages.get(runId);

  if (content === undefined) {
    return `<pre>Loading...</pre>`;
  }

  if (content === null) {
    return `<div class="empty">Workflow file is not available.</div>`;
  }

  const expanded = state.expandedWorkflowFiles.has(runId);
  const lines = splitLines(content);
  const hasMore = lines.length > previewLineCount;
  const shown = expanded || !hasMore ? content : lines.slice(0, previewLineCount).join("\n");
  const action = expanded ? "collapse" : "expand";
  const label = expanded ? "Show less" : `Show full file (${lines.length} lines)`;

  return `
    <pre>${escapeHtml(shown)}</pre>
    ${hasMore ? `<div class="workflow-file-actions"><button class="text-button" type="button" data-workflow-action="${action}" data-run-id="${escapeHtml(runId)}">${label}</button></div>` : ""}
    ${message ? `<div class="inline-message">${escapeHtml(message)}</div>` : ""}
  `;
}

function updateWorkflowFileShell(runId) {
  const section = document.querySelector(`[data-workflow-file-run="${cssEscape(runId)}"]`);
  const body = section?.querySelector(".workflow-file-body");
  if (body) {
    body.innerHTML = renderWorkflowFileBody(runId);
  }
}

function renderSettings() {
  const cacheEntries = state.cache.entries ?? [];
  el.settingsView.innerHTML = `
    <section class="summary">
      <div class="summary-head"><h2>Runtime</h2></div>
      <div class="settings-grid">
        ${summaryCell("Project root", state.health?.projectRoot ?? "")}
        ${summaryCell("ACTIO_HOME", state.health?.actioHome ?? "")}
        ${summaryCell("Server URL", state.health?.serverUrl ?? "")}
        ${summaryCell("Cache root", state.health?.cacheRoot ?? state.cache.cacheRoot ?? "")}
      </div>
    </section>

    <section class="summary">
      <div class="summary-head">
        <h2>Action cache</h2>
        <button id="clear-cache" class="danger-button" type="button">Clear cache</button>
      </div>
      ${state.cacheMessage ? `<div class="inline-message">${escapeHtml(state.cacheMessage)}</div>` : ""}
      ${renderCacheEntries(cacheEntries)}
    </section>
  `;

  const clearButton = document.querySelector("#clear-cache");
  clearButton?.addEventListener("click", clearCache);
}

function renderCacheEntries(entries) {
  if (entries.length === 0) {
    return `<div class="empty">No cache entries.</div>`;
  }

  return `
    <div class="cache-list">
      ${entries.map(entry => `
        <article class="cache-row">
          <div>
            <div class="cache-title">${escapeHtml(entry.kind)}:${escapeHtml(entry.uses)}</div>
            <div class="muted">${escapeHtml(entry.sourcePath)}</div>
          </div>
          <div class="cache-meta">
            ${entry.pinnedIdentity ? `<span class="pill">pinned: ${escapeHtml(entry.pinnedIdentity)}</span>` : ""}
            ${entry.mutablePart ? `<span class="pill">mutable: ${escapeHtml(entry.mutablePart)}</span>` : ""}
            <span class="pill">last used: ${formatDate(entry.lastUsedAt)}</span>
          </div>
          <div class="muted">${escapeHtml(entry.cachePath)}</div>
        </article>
      `).join("")}
    </div>
  `;
}

async function clearCache() {
  if (!confirm("Clear all Actio action cache entries?")) {
    return;
  }

  try {
    const result = await fetchJson("/api/cache", { method: "DELETE" });
    state.cacheMessage = `Removed ${result.removed} cache ${result.removed === 1 ? "entry" : "entries"}.`;
    state.cache = await fetchJson("/api/cache");
  } catch (error) {
    state.cacheMessage = `Cache could not be cleared: ${error.message}`;
  }

  renderSettings();
}

function wireLogButtons(run) {
  document.querySelectorAll(".log-button[data-log-key][data-job][data-step]").forEach(button => {
    button.addEventListener("click", async () => {
      const key = button.dataset.logKey;
      const target = Array.from(document.querySelectorAll(".log-view"))
        .find(item => item.dataset.logKey === key);

      if (!target) {
        return;
      }

      if (state.openLogs.has(key)) {
        state.openLogs.delete(key);
        target.hidden = true;
        return;
      }

      state.openLogs.add(key);
      target.hidden = false;
      await refreshLog(run.runId, button.dataset.job, button.dataset.step, target, key);
    });
  });
}

async function refreshOpenLogs(run) {
  const targets = Array.from(document.querySelectorAll(".log-view:not([hidden])"));

  await Promise.all(targets.map(target =>
    refreshLog(run.runId, target.dataset.job, target.dataset.step, target, target.dataset.logKey)));
}

async function refreshLog(runId, job, step, target, key) {
  try {
    const content = await fetchText(`/api/runs/${encodeURIComponent(runId)}/logs?job=${encodeURIComponent(job)}&step=${encodeURIComponent(step)}`);
    state.logContents.set(key, content);
    target.textContent = content;
  } catch {
    target.textContent = "Log is not available.";
  }
}

async function loadWorkflowFile(runId) {
  if (state.workflowFiles.has(runId)) {
    updateWorkflowFileShell(runId);
    return;
  }

  try {
    const content = await fetchText(`/api/runs/${encodeURIComponent(runId)}/workflow-file`);
    state.workflowFiles.set(runId, content);
  } catch {
    state.workflowFiles.set(runId, null);
  }

  updateWorkflowFileShell(runId);
}

async function ensureWorkflowFileContent(runId) {
  if (!state.workflowFiles.has(runId)) {
    await loadWorkflowFile(runId);
  }

  const content = state.workflowFiles.get(runId);
  if (content === null || content === undefined) {
    throw new Error("Workflow file is not available.");
  }

  return content;
}

async function copyWorkflowFile(runId) {
  try {
    await copyText(await ensureWorkflowFileContent(runId));
    setWorkflowMessage(runId, "Copied workflow file.");
  } catch (error) {
    setWorkflowMessage(runId, `Copy failed: ${error.message}`);
  }
}

function downloadWorkflowFile(runId) {
  const link = document.createElement("a");
  link.href = `/api/runs/${encodeURIComponent(runId)}/workflow-file/download`;
  link.download = "";
  document.body.append(link);
  link.click();
  link.remove();
}

async function openWorkflowEditor(runId) {
  let content;
  try {
    content = await ensureWorkflowFileContent(runId);
  } catch (error) {
    setWorkflowMessage(runId, error.message);
    return;
  }

  const overlay = document.createElement("div");
  overlay.className = "modal-backdrop";
  overlay.innerHTML = `
    <div class="modal" role="dialog" aria-modal="true" aria-label="Edit workflow file">
      <div class="modal-head">
        <h2>Edit workflow file</h2>
        <button class="icon-button" type="button" title="Close" aria-label="Close" data-editor-cancel>${closeIcon()}</button>
      </div>
      <textarea class="workflow-editor" spellcheck="false">${escapeHtml(content)}</textarea>
      <div class="modal-message muted" data-editor-message></div>
      <div class="modal-actions">
        <button class="text-button" type="button" data-editor-cancel>Cancel</button>
        <button class="primary-button" type="button" data-editor-save>Save</button>
      </div>
    </div>
  `;

  document.body.append(overlay);
  const textarea = overlay.querySelector("textarea");
  textarea.focus();

  overlay.querySelectorAll("[data-editor-cancel]").forEach(button => {
    button.addEventListener("click", () => overlay.remove());
  });

  overlay.addEventListener("click", event => {
    if (event.target === overlay) {
      overlay.remove();
    }
  });

  overlay.querySelector("[data-editor-save]").addEventListener("click", async () => {
    const message = overlay.querySelector("[data-editor-message]");
    const saveButton = overlay.querySelector("[data-editor-save]");
    saveButton.disabled = true;
    message.textContent = "Saving...";

    try {
      const response = await fetch(`/api/runs/${encodeURIComponent(runId)}/workflow-file`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ content: textarea.value })
      });
      const result = await response.json();
      if (!response.ok || !result.success) {
        message.textContent = (result.errors ?? ["Workflow file could not be saved."]).join("\n");
        return;
      }

      state.workflowFiles.set(runId, textarea.value);
      setWorkflowMessage(runId, "Saved workflow file.");
      overlay.remove();
      await refreshData();
    } catch (error) {
      message.textContent = `Workflow file could not be saved: ${error.message}`;
    } finally {
      saveButton.disabled = false;
    }
  });
}

function setWorkflowMessage(runId, message) {
  state.workflowMessages.set(runId, message);
  updateWorkflowFileShell(runId);
}

async function selectRun(runId, options = {}) {
  state.currentView = "runs";
  state.selectedRunId = runId;

  if (options.replaceHistory) {
    history.replaceState(null, "", `/runs/${encodeURIComponent(runId)}`);
  } else {
    history.pushState(null, "", `/runs/${encodeURIComponent(runId)}`);
  }

  if (options.renderNow !== false) {
    renderRuns();
    await renderDetail();
  }
}

function scheduleRefresh(delay = nextPollDelay()) {
  window.clearTimeout(state.refreshTimer);
  state.refreshTimer = window.setTimeout(refreshFromPoll, delay);
}

async function refreshFromPoll() {
  if (document.hidden) {
    scheduleRefresh();
    return;
  }

  try {
    await refreshData();
  } catch {
  } finally {
    scheduleRefresh();
  }
}

function nextPollDelay() {
  if (document.hidden) {
    return pollHiddenMilliseconds;
  }

  return hasActiveRun() ? pollActiveMilliseconds : pollIdleMilliseconds;
}

function hasActiveRun() {
  return state.runs.some(run => isActiveStatus(run.status));
}

function isActiveStatus(status) {
  return String(status ?? "").toLowerCase() === "running";
}

function filteredRuns() {
  if (!state.filter) {
    return state.runs;
  }

  return state.runs.filter(run => [
    run.workflowName,
    run.workflowPath,
    run.runId,
    run.status
  ].some(value => (value ?? "").toLowerCase().includes(state.filter)));
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}`);
  }

  return response.json();
}

async function fetchText(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}`);
  }

  return response.text();
}

function summaryCell(label, value) {
  return `
    <div class="summary-cell">
      <div class="summary-label">${escapeHtml(label)}</div>
      <div class="summary-value" title="${escapeHtml(String(value))}">${escapeHtml(String(value))}</div>
    </div>
  `;
}

function statusClass(status) {
  const value = (status ?? "").toLowerCase();
  if (value === "success") return "status-success";
  if (value === "failed") return "status-failed";
  if (value === "skipped") return "status-skipped";
  if (value === "running") return "status-running";
  return "";
}

function formatDate(value) {
  return new Date(value).toLocaleString();
}

function formatDuration(milliseconds) {
  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }

  const seconds = Math.round(milliseconds / 1000);
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return minutes > 0 ? `${minutes}m ${rest}s` : `${seconds}s`;
}

function formatTriggers(triggers) {
  return triggers.map(trigger => {
    const keys = Object.keys(trigger.configuration?.properties ?? {});
    return keys.length ? `${trigger.eventName} (${keys.join(", ")})` : trigger.eventName;
  }).join(", ");
}

function shortRunId(runId) {
  return runId.length > 12 ? runId.slice(0, 12) : runId;
}

function logKey(runId, job, step) {
  return `${runId}|${job}|${step}`;
}

function selectedRunPath() {
  return state.selectedRunId ? `/runs/${encodeURIComponent(state.selectedRunId)}` : "/";
}

function setTheme(theme) {
  state.theme = theme === "light" ? "light" : "dark";
  applyTheme(state.theme);

  try {
    localStorage.setItem(themeStorageKey, state.theme);
  } catch {
  }
}

function loadTheme() {
  try {
    return localStorage.getItem(themeStorageKey) === "light" ? "light" : "dark";
  } catch {
    return "dark";
  }
}

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  el.themeToggle.querySelectorAll("[data-theme-choice]").forEach(button => {
    const active = button.dataset.themeChoice === theme;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", active ? "true" : "false");
  });
}

function splitLines(content) {
  return String(content ?? "").split(/\r?\n/);
}

async function copyText(text) {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.style.position = "fixed";
  textarea.style.opacity = "0";
  document.body.append(textarea);
  textarea.select();
  const copied = document.execCommand("copy");
  textarea.remove();

  if (!copied) {
    throw new Error("Clipboard is not available.");
  }
}

function copyIcon() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="8" y="8" width="11" height="11" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg>`;
}

function downloadIcon() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v12"></path><path d="m7 10 5 5 5-5"></path><path d="M5 21h14"></path></svg>`;
}

function closeIcon() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18"></path><path d="m6 6 12 12"></path></svg>`;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function cssEscape(value) {
  return CSS.escape(String(value ?? ""));
}
