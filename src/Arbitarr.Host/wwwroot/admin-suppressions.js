// Arbitarr admin suppressions view (wave-C item 3, plan M7 UI list item 3, P3): admin-gated (D2,
// AdminApiKeyFilter) read-only page that hits GET /api/admin/suppressions — reading straight from
// the append-only SuppressionAuditLogEntry log (already written by FilterStage, M4-5). No new
// suppression logic runs here; this page only renders what SuppressionViewEndpoint returns.

async function fetchJson(url, adminKey) {
  const response = await fetch(url, {
    headers: { Accept: "application/json", "X-Admin-Api-Key": adminKey },
  });
  if (!response.ok) {
    throw new Error(`${url} responded ${response.status}`);
  }
  return response.json();
}

function escapeHtml(value) {
  const div = document.createElement("div");
  div.textContent = value;
  return div.innerHTML;
}

function renderSuppressions(entries) {
  const body = document.getElementById("suppressions-body");

  if (!entries || entries.length === 0) {
    body.innerHTML = '<p class="empty-text">No suppressed or de-ranked results.</p>';
    return;
  }

  const rows = entries
    .map(
      (e) => `<tr>
        <td>${escapeHtml(new Date(e.occurredAt).toLocaleString())}</td>
        <td>${escapeHtml(e.releaseIdentifier)}</td>
        <td>${escapeHtml(e.queryKey)}</td>
        <td>${escapeHtml(e.layer)}</td>
        <td>${escapeHtml(e.reason)}</td>
        <td>${e.shadowMode ? "yes" : "no"}</td>
      </tr>`,
    )
    .join("");

  body.innerHTML = `<table>
    <thead><tr><th>Occurred At</th><th>Release</th><th>Query Key</th><th>Layer</th><th>Reason</th><th>Shadow Mode</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>`;
}

function renderError(err) {
  document.getElementById("suppressions-body").innerHTML =
    `<p class="error-text">Load failed: ${escapeHtml(err.message)}</p>`;
}

async function handleSubmit(event) {
  event.preventDefault();

  const adminKey = document.getElementById("admin-key").value;
  const queryKey = document.getElementById("query-key").value.trim();

  const params = new URLSearchParams();
  if (queryKey) {
    params.set("queryKey", queryKey);
  }

  try {
    const entries = await fetchJson(`/api/admin/suppressions?${params.toString()}`, adminKey);
    renderSuppressions(entries);
  } catch (err) {
    renderError(err);
  }
}

document.getElementById("filter-form").addEventListener("submit", handleSubmit);
