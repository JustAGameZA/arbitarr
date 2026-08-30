// Arbitarr admin settings page (M7-8, AC24): admin-gated (D2, AdminApiKeyFilter) page that reads
// and writes GET/PUT /api/admin/settings. Bounds (Min/Max) and the RequiresRestart flag come
// straight from the server payload (AdminSettingsEndpoints.SettingCatalogEntryResponse) — this
// script never hardcodes a floor/ceiling, it only mirrors whatever the server reports so the
// client-side check can never drift from AC24's actual enforced bounds.

async function fetchJson(url, adminKey, options) {
  const response = await fetch(url, {
    ...options,
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
      "X-Admin-Api-Key": adminKey,
      ...(options && options.headers),
    },
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    const message = body && body.error ? body.error : `${url} responded ${response.status}`;
    throw new Error(message);
  }
  if (response.status === 204) {
    return null;
  }
  return response.json();
}

function escapeHtml(value) {
  const div = document.createElement("div");
  div.textContent = value;
  return div.innerHTML;
}

function parseValue(entry, rawValue) {
  return entry.isBoolean ? rawValue === "true" : rawValue;
}

function isWithinBounds(entry, rawValue) {
  if (entry.isBoolean) {
    return rawValue === "true" || rawValue === "false";
  }

  // Durations sort lexicographically incorrectly, so bound-check numerically where possible
  // (AiVerdictCacheRowCeiling is a plain integer; everything else is a TimeSpan string). For
  // TimeSpan-shaped values we fall back to letting the server be the source of truth and only
  // reject obviously-empty input here — the PUT call still enforces the real bound server-side.
  if (entry.min !== null && entry.max !== null) {
    const numericMin = Number(entry.min);
    const numericMax = Number(entry.max);
    const numericValue = Number(rawValue);
    if (!Number.isNaN(numericMin) && !Number.isNaN(numericMax) && !Number.isNaN(numericValue)) {
      return numericValue >= numericMin && numericValue <= numericMax;
    }
  }

  return rawValue.trim().length > 0;
}

function renderEntry(entry) {
  const restartBadge = entry.requiresRestart
    ? '<span class="requires-restart-badge">Requires restart</span>'
    : "";

  const boundsText = entry.isBoolean
    ? ""
    : `<p class="bounds-text">Min: ${entry.min === null ? "none" : escapeHtml(entry.min)} &middot; Max: ${entry.max === null ? "none" : escapeHtml(entry.max)}</p>`;

  const input = entry.isBoolean
    ? `<select id="value-${entry.key}">
         <option value="true" ${entry.value === "true" ? "selected" : ""}>true</option>
         <option value="false" ${entry.value === "false" ? "selected" : ""}>false</option>
       </select>`
    : `<input type="text" id="value-${entry.key}" value="${escapeHtml(entry.value)}" />`;

  return `<div class="setting-row" data-key="${entry.key}">
    <h3>${escapeHtml(entry.displayName)} ${restartBadge}</h3>
    <p class="rationale-text">${escapeHtml(entry.rationale)}</p>
    ${boundsText}
    <label>
      Value
      ${input}
    </label>
    <button type="button" class="save-setting" data-key="${entry.key}">Save</button>
    <p class="status-text" id="status-${entry.key}"></p>
  </div>`;
}

function renderSettings(entries) {
  const body = document.getElementById("settings-body");

  if (!entries || entries.length === 0) {
    body.innerHTML = '<p class="empty-text">No settings returned.</p>';
    return;
  }

  const groups = new Map();
  for (const entry of entries) {
    if (!groups.has(entry.group)) {
      groups.set(entry.group, []);
    }
    groups.get(entry.group).push(entry);
  }

  let html = "";
  for (const [group, groupEntries] of groups) {
    html += `<h2 class="settings-group-heading">${escapeHtml(group)}</h2>`;
    html += groupEntries.map(renderEntry).join("");
  }

  body.innerHTML = html;

  for (const entry of entries) {
    document
      .querySelector(`.save-setting[data-key="${entry.key}"]`)
      .addEventListener("click", () => handleSave(entry));
  }
}

let currentEntries = [];

async function handleLoad() {
  const adminKey = document.getElementById("admin-key").value;
  const body = document.getElementById("settings-body");

  try {
    currentEntries = await fetchJson("/api/admin/settings", adminKey);
    renderSettings(currentEntries);
  } catch (err) {
    body.innerHTML = `<p class="error-text">Failed to load settings: ${escapeHtml(err.message)}</p>`;
  }
}

async function handleSave(entry) {
  const adminKey = document.getElementById("admin-key").value;
  const input = document.getElementById(`value-${entry.key}`);
  const statusEl = document.getElementById(`status-${entry.key}`);
  const rawValue = input.value;

  if (!isWithinBounds(entry, rawValue)) {
    statusEl.textContent = `Value is outside the allowed range (min: ${entry.min ?? "none"}, max: ${entry.max ?? "none"}).`;
    statusEl.className = "status-text error-text";
    return;
  }

  try {
    await fetchJson(`/api/admin/settings/${entry.key}`, adminKey, {
      method: "PUT",
      body: JSON.stringify({ value: parseValue(entry, rawValue).toString() }),
    });
    statusEl.textContent = entry.requiresRestart
      ? "Saved. This setting requires a restart to take effect."
      : "Saved.";
    statusEl.className = "status-text success-text";
  } catch (err) {
    statusEl.textContent = `Save failed: ${err.message}`;
    statusEl.className = "status-text error-text";
  }
}

document.getElementById("load-settings").addEventListener("click", handleLoad);
