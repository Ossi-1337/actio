const state = {
  workflows: [],
  runs: [],
  selectedRunId: location.pathname.startsWith("/runs/") ? decodeURIComponent(location.pathname.split("/").pop()) : null,
  filter: ""
};

const el = {
  workflows: document.querySelector("#workflow-list"),
  runs: document.querySelector("#run-list"),
  detail: document.querySelector("#detail"),
  title: document.querySelector("#page-title"),
  projectRoot: document.querySelector("#project-root"),
  runCount: document.querySelector("#run-count"),
  filter: document.querySelector("#run-filter")
};

el.filter.addEventListener("input", () => {
  state.filter = el.filter.value.trim().toLowerCase();
  renderRuns();
});

window.addEventListener("popstate", () => {
  state.selectedRunId = location.pathname.startsWith("/runs/") ? decodeURIComponent(location.pathname.split("/").pop()) : null;
  render();
});

load();

async function load() {
  const [health, workflows, runs] = await Promise.all([
    fetchJson("/api/health"),
    fetchJson("/api/workflows"),
    fetchJson("/api/runs")
  ]);

  state.workflows = workflows;
  state.runs = runs;
  el.projectRoot.textContent = health.projectRoot;

  if (!state.selectedRunId && runs.length > 0) {
    state.selectedRunId = runs[0].runId;
    history.replaceState(null, "", `/runs/${encodeURIComponent(state.selectedRunId)}`);
  }

  render();
}

function render() {
  renderWorkflows();
  renderRuns();
  renderDetail();
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
    item.addEventListener("click", event => {
      event.preventDefault();
      state.selectedRunId = item.dataset.run;
      history.pushState(null, "", `/runs/${encodeURIComponent(state.selectedRunId)}`);
      render();
    });
  });
}

async function renderDetail() {
  if (!state.selectedRunId) {
    el.detail.innerHTML = `<section class="summary"><div class="empty">No run selected.</div></section>`;
    return;
  }

  let run;
  try {
    run = await fetchJson(`/api/runs/${encodeURIComponent(state.selectedRunId)}`);
  } catch {
    el.title.textContent = "Workflow runs";
    el.detail.innerHTML = `<section class="summary"><div class="empty">Run is not available.</div></section>`;
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
  loadWorkflowFile(run.runId);
}

function renderSummary(run) {
  return `
    <section class="summary">
      <div class="summary-head">
        <h2>${escapeHtml(run.workflowName)}</h2>
        <span class="pill">${escapeHtml(run.status)}</span>
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
    return `<section class="summary"><div class="empty">No jobs recorded.</div></section>`;
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
        <span class="pill">${escapeHtml(job.status)}</span>
      </div>
      <div class="job-body">
        ${job.errors.length ? `<div class="empty">${job.errors.map(escapeHtml).join("<br>")}</div>` : ""}
        ${job.steps.map(step => `
          <div class="step-row">
            <span class="status-dot ${statusClass(step.status)}"></span>
            <span>
              <span class="job-name">${escapeHtml(step.name)}</span>
              <span class="run-sub muted">${escapeHtml(step.status)} · ${formatDuration(step.durationMilliseconds)}</span>
            </span>
            ${step.logPath ? `<button class="log-button" data-job="${escapeHtml(job.name)}" data-step="${escapeHtml(step.name)}">Log</button>` : `<span class="muted">No log</span>`}
          </div>
          <pre class="log-view" hidden data-log="${escapeHtml(job.name)}|${escapeHtml(step.name)}"></pre>
        `).join("")}
      </div>
    </section>
  `).join("");
}

function renderWorkflowFileShell(run) {
  return `
    <section class="workflow-file">
      <div class="summary-head"><h2>Workflow file</h2></div>
      <pre id="workflow-file-${escapeHtml(run.runId)}">Loading...</pre>
    </section>
  `;
}

function wireLogButtons(run) {
  document.querySelectorAll("[data-job][data-step]").forEach(button => {
    button.addEventListener("click", async () => {
      const job = button.dataset.job;
      const step = button.dataset.step;
      const target = Array.from(document.querySelectorAll("[data-log]"))
        .find(item => item.dataset.log === `${job}|${step}`);

      if (!target) {
        return;
      }

      if (target.hidden) {
        try {
          target.textContent = await fetchText(`/api/runs/${encodeURIComponent(run.runId)}/logs?job=${encodeURIComponent(job)}&step=${encodeURIComponent(step)}`);
        } catch {
          target.textContent = "Log is not available.";
        }
      }

      target.hidden = !target.hidden;
    });
  });
}

async function loadWorkflowFile(runId) {
  const target = document.querySelector(`#workflow-file-${cssEscape(runId)}`);
  if (!target) {
    return;
  }

  try {
    target.textContent = await fetchText(`/api/runs/${encodeURIComponent(runId)}/workflow-file`);
  } catch {
    target.textContent = "Workflow file is not available.";
  }
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
