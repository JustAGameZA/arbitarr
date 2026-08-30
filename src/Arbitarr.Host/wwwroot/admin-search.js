// Arbitarr admin ad-hoc search (M7-1, non-AI half): admin-gated (D2, AdminApiKeyFilter) page that
// hits GET /api/admin/search — the same PaginationSnapshotService/UpstreamMergeStage path as
// /torznab/api, rendered as JSON for this dashboard instead of Torznab/Newznab XML.
//
// AC14b's synchronous-AI-arbitration opt-in is intentionally NOT wired here: it waits on M5, and
// per AC6a this page must never reference Arbitarr.Ai or call anything AI-related. The "Run AI
// arbitration synchronously" checkbox in admin-search.html stays disabled/unchecked as a visible
// placeholder only.

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

function renderProvenance(provenance) {
  const body = document.getElementById("provenance-body");
  const cacheLine = `<p>Source: <strong>${provenance.fromCache ? "cache hit" : "fresh merge"}</strong></p>`;
  const rateLimited =
    provenance.rateLimitedSources && provenance.rateLimitedSources.length > 0
      ? `<p>Rate-limited sources: ${escapeHtml(provenance.rateLimitedSources.join(", "))}</p>`
      : "<p>Rate-limited sources: none</p>";
  body.innerHTML = cacheLine + rateLimited;
}

function renderResults(releases) {
  const body = document.getElementById("results-body");

  if (!releases || releases.length === 0) {
    body.innerHTML = '<p class="empty-text">No releases found.</p>';
    return;
  }

  const rows = releases
    .map(
      (r) => `<tr>
        <td>${escapeHtml(r.title)}</td>
        <td>${escapeHtml(r.guid)}</td>
        <td>${r.size}</td>
        <td>${escapeHtml((r.category || []).join(", "))}</td>
        <td>${escapeHtml(r.sourceName)}</td>
        <td>${escapeHtml(new Date(r.pubDate).toLocaleString())}</td>
      </tr>`,
    )
    .join("");

  body.innerHTML = `<table>
    <thead><tr><th>Title</th><th>Guid</th><th>Size</th><th>Category</th><th>Source</th><th>Published</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>`;
}

function renderError(err) {
  document.getElementById("results-body").innerHTML =
    `<p class="error-text">Search failed: ${escapeHtml(err.message)}</p>`;
  document.getElementById("provenance-body").innerHTML = "";
}

function buildQueryString() {
  const params = new URLSearchParams();
  const fields = ["q", "tvdbid", "tmdbid", "season", "ep", "cat"];
  for (const field of fields) {
    const value = document.getElementById(field).value.trim();
    if (value) {
      params.set(field, value);
    }
  }
  return params.toString();
}

async function handleSubmit(event) {
  event.preventDefault();

  const adminKey = document.getElementById("admin-key").value;
  const queryString = buildQueryString();

  try {
    const data = await fetchJson(`/api/admin/search?${queryString}`, adminKey);
    renderResults(data.releases);
    renderProvenance(data.provenance);
  } catch (err) {
    renderError(err);
  }
}

document.getElementById("search-form").addEventListener("submit", handleSubmit);
