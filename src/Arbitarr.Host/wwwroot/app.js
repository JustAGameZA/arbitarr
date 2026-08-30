// Arbitarr dashboard (M2, "7-lite"): read-only polling UI. No auth, no mutation calls —
// every request here is a GET against a PublicRead-classified endpoint (see
// src/Arbitarr.Api/Routing/RouteClassification.cs). Ad-hoc search and admin actions are
// explicitly out of scope for this milestone (D1) and are NOT wired here.

const POLL_INTERVAL_MS = 10_000;

async function fetchJson(url) {
  const response = await fetch(url, { headers: { Accept: "application/json" } });
  if (!response.ok) {
    throw new Error(`${url} responded ${response.status}`);
  }
  return response.json();
}

function renderStatus(data) {
  const body = document.getElementById("status-body");
  const workerLine = `<p>Worker: <strong>${escapeHtml(data.workerStatus)}</strong></p>`;

  if (!data.sources || data.sources.length === 0) {
    body.innerHTML = workerLine + '<p class="empty-text">No sources reporting yet.</p>';
    return;
  }

  const rows = data.sources
    .map((s) => {
      const stateClass = `state-${s.state}`;
      return `<tr>
        <td>${escapeHtml(s.sourceName)}</td>
        <td class="${stateClass}">${escapeHtml(s.state)}</td>
        <td>${s.consecutiveFailures}</td>
        <td>${s.lastError ? escapeHtml(s.lastError) : ""}</td>
      </tr>`;
    })
    .join("");

  body.innerHTML = `${workerLine}<table>
    <thead><tr><th>Source</th><th>State</th><th>Failures</th><th>Last Error</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>`;
}

function renderSearches(entries) {
  const body = document.getElementById("searches-body");

  if (!entries || entries.length === 0) {
    body.innerHTML = '<p class="empty-text">No searches recorded yet.</p>';
    return;
  }

  const rows = entries
    .map(
      (e) => `<tr>
        <td>${escapeHtml(new Date(e.receivedAt).toLocaleString())}</td>
        <td>${escapeHtml(e.query)}</td>
        <td>${e.resolvedIdentity ? escapeHtml(e.resolvedIdentity) : ""}</td>
        <td>${e.resultCount}</td>
        <td>${e.elapsedMilliseconds}ms</td>
        <td>${e.band ? escapeHtml(e.band) : ""}</td>
      </tr>`,
    )
    .join("");

  body.innerHTML = `<table>
    <thead><tr><th>Time</th><th>Query</th><th>Identity</th><th>Results</th><th>Elapsed</th><th>Band</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>`;
}

function renderConfig(config) {
  const body = document.getElementById("config-body");
  const rows = Object.entries(config)
    .map(([key, value]) => `<tr><td>${escapeHtml(key)}</td><td>${escapeHtml(String(value))}</td></tr>`)
    .join("");

  body.innerHTML = `<table>
    <thead><tr><th>Setting</th><th>Value</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>`;
}

function renderError(panelBodyId, err) {
  document.getElementById(panelBodyId).innerHTML =
    `<p class="error-text">Failed to load: ${escapeHtml(err.message)}</p>`;
}

function escapeHtml(value) {
  const div = document.createElement("div");
  div.textContent = value;
  return div.innerHTML;
}

async function refreshStatus() {
  try {
    renderStatus(await fetchJson("/api/status"));
  } catch (err) {
    renderError("status-body", err);
  }
}

async function refreshSearches() {
  try {
    renderSearches(await fetchJson("/api/searches/recent"));
  } catch (err) {
    renderError("searches-body", err);
  }
}

async function refreshConfig() {
  try {
    renderConfig(await fetchJson("/api/config/effective"));
  } catch (err) {
    renderError("config-body", err);
  }
}

function refreshAll() {
  refreshStatus();
  refreshSearches();
  refreshConfig();
}

refreshAll();
setInterval(refreshAll, POLL_INTERVAL_MS);
