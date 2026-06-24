const pollActiveMilliseconds = 2000;
const pollIdleMilliseconds = 5000;
const pollHiddenMilliseconds = 15000;
const themeStorageKey = "actio-theme";

const state = {
  workflows: [],
  runs: [],
  selectedRunId: location.pathname.startsWith("/runs/") ? decodeURIComponent(location.pathname.split("/").pop()) : null,
  filter: "",
  projectRoot: "",
  openLogs: new Set(),
  logContents: new Map(),
  workflowFiles: new Map(),
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
  themeToggle: document.querySelector("#theme-toggle")
};

applyTheme(state.theme);

el.filter.addEventListener("input", () => {
  state.filter = el.filter.value.trim().toLowerCase();
  renderRuns();
});

el.themeToggle.addEventListener("click", event => {
  const button = event.target.closest("[data-theme-choice]");
  if (!button) {
    return;
  }

  setTheme(button.dataset.themeChoice);
});

window.addEventListener("popstate", () => {
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
  const [health, workflows, runs] = await Promise.all([
    fetchJson("/api/health"),
    fetchJson("/api/workflows"),
    fetchJson("/api/runs")
  ]);

  state.projectRoot = health.projectRoot;
  state.workflows = workflows;
  state.runs = runs;
  el.projectRoot.textContent = state.projectRoot;

  if (options.selectLatestRun && !state.selectedRunId && runs.length > 0) {
    selectRun(runs[0].runId, { replaceHistory: true, renderNow: false });
  }

  await render();
}

async function render() {
  renderWorkflows();
  renderRuns();
  await renderDetail();
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
      state.filter = item.dataset.workflow.toLowerCase();
      el.filter.value = item.dataset.workflow;
      renderRuns();
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

    el.title.textContent = "Workflow runs";
    el.detail.innerHTML = `<section class="summary"><div class="empty">Run is not available yet.</div></section>`;
    return;
  }

  if (requestId !== state.detailRequestId) {
    return;
  }

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
  const content = state.workflowFiles.get(run.runId) ?? "Loading...";

  return `
    <section class="workflow-file">
      <div class="summary-head"><h2>Workflow file</h2></div>
      <pre id="workflow-file-${escapeHtml(run.runId)}">${escapeHtml(content)}</pre>
    </section>
  `;
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
  const target = document.querySelector(`#workflow-file-${cssEscape(runId)}`);
  if (!target) {
    return;
  }

  if (state.workflowFiles.has(runId)) {
    target.textContent = state.workflowFiles.get(runId);
    return;
  }

  try {
    const content = await fetchText(`/api/runs/${encodeURIComponent(runId)}/workflow-file`);
    state.workflowFiles.set(runId, content);
    target.textContent = content;
  } catch {
    target.textContent = "Workflow file is not available.";
  }
}

async function selectRun(runId, options = {}) {
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

async function fetchJson(url) {
  const response = await fetch(url);
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

function shortRunId(runId) {
  return runId.length > 12 ? runId.slice(0, 12) : runId;
}

function logKey(runId, job, step) {
  return `${runId}|${job}|${step}`;
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
