// Arbitarr admin rules page (wave-C item 4, R11/AC24): admin-gated (D2, AdminApiKeyFilter) page
// over GET/POST/PUT/DELETE /api/admin/rules and POST /api/admin/rules/test. All data (rule fields,
// verdicts, error reasons) is rendered from server responses only — this script never hardcodes
// pattern-safety or count-cap logic, it only surfaces whatever the server rejects (with a reason)
// or accepts.
//
// Import/export here formats/parses the same pipe-delimited `name|isAllow|precedence|pattern` line
// shape as Arbitarr.Core.Filtering.RuleExporter/RuleImporter (reused server-side by every other rule
// consumer) but does so client-side against the existing create endpoint, rather than duplicating
// RuleImporter/RuleExporter's parsing logic in a second server-side endpoint — each imported line
// still goes through the same server-side pattern/count validation as a normal "Add rule" call.

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

const PRECEDENCE_NAMES = ["Lowest", "Low", "Normal", "High", "Highest"];

function renderRule(rule) {
  return `<div class="setting-row" data-key="${rule.id}">
    <h3>${escapeHtml(rule.name)} <span class="requires-restart-badge">${rule.isAllow ? "Allow" : "Reject"}</span></h3>
    <p class="rationale-text">Pattern: <code>${escapeHtml(rule.pattern)}</code></p>
    <p class="bounds-text">Precedence: ${escapeHtml(PRECEDENCE_NAMES[rule.precedence] ?? String(rule.precedence))} &middot; Enabled: ${rule.enabled}</p>
    <button type="button" class="delete-rule" data-id="${rule.id}">Delete</button>
    <p class="status-text" id="status-rule-${rule.id}"></p>
  </div>`;
}

let currentRules = [];

function renderRules(rules) {
  const body = document.getElementById("rules-body");

  if (!rules || rules.length === 0) {
    body.innerHTML = '<p class="empty-text">No rules yet.</p>';
    return;
  }

  body.innerHTML = rules.map(renderRule).join("");

  for (const rule of rules) {
    document
      .querySelector(`.delete-rule[data-id="${rule.id}"]`)
      .addEventListener("click", () => handleDelete(rule));
  }
}

async function handleLoad() {
  const adminKey = document.getElementById("admin-key").value;
  const body = document.getElementById("rules-body");

  try {
    currentRules = await fetchJson("/api/admin/rules", adminKey);
    renderRules(currentRules);
  } catch (err) {
    body.innerHTML = `<p class="error-text">Failed to load rules: ${escapeHtml(err.message)}</p>`;
  }
}

async function handleDelete(rule) {
  const adminKey = document.getElementById("admin-key").value;
  const statusEl = document.getElementById(`status-rule-${rule.id}`);

  try {
    await fetchJson(`/api/admin/rules/${rule.id}`, adminKey, { method: "DELETE" });
    await handleLoad();
  } catch (err) {
    statusEl.textContent = `Delete failed: ${err.message}`;
    statusEl.className = "status-text error-text";
  }
}

async function handleAdd() {
  const adminKey = document.getElementById("admin-key").value;
  const statusEl = document.getElementById("new-rule-status");

  const request = {
    name: document.getElementById("new-rule-name").value,
    pattern: document.getElementById("new-rule-pattern").value,
    precedence: Number(document.getElementById("new-rule-precedence").value),
    isAllow: document.getElementById("new-rule-action").value === "true",
    enabled: document.getElementById("new-rule-enabled").checked,
  };

  try {
    await fetchJson("/api/admin/rules", adminKey, {
      method: "POST",
      body: JSON.stringify(request),
    });
    statusEl.textContent = "Added.";
    statusEl.className = "status-text success-text";
    await handleLoad();
  } catch (err) {
    statusEl.textContent = `Add failed: ${err.message}`;
    statusEl.className = "status-text error-text";
  }
}

async function handleTest() {
  const adminKey = document.getElementById("admin-key").value;
  const statusEl = document.getElementById("test-rule-status");

  const request = {
    name: document.getElementById("test-rule-name").value,
    pattern: document.getElementById("test-rule-pattern").value,
    precedence: Number(document.getElementById("test-rule-precedence").value),
    isAllow: document.getElementById("test-rule-action").value === "true",
    title: document.getElementById("test-rule-title").value,
  };

  try {
    const result = await fetchJson("/api/admin/rules/test", adminKey, {
      method: "POST",
      body: JSON.stringify(request),
    });
    statusEl.textContent = `Verdict: ${result.verdict}`;
    statusEl.className = "status-text success-text";
  } catch (err) {
    statusEl.textContent = `Test failed: ${err.message}`;
    statusEl.className = "status-text error-text";
  }
}

function escapePipeField(value) {
  return String(value).replace(/\\/g, "\\\\").replace(/\|/g, "\\|");
}

function handleExport() {
  const textarea = document.getElementById("import-export-text");
  textarea.value = currentRules
    .map((r) => `${escapePipeField(r.name)}|${r.isAllow}|${r.precedence}|${escapePipeField(r.pattern)}`)
    .join("\n");
  const statusEl = document.getElementById("import-export-status");
  statusEl.textContent = `Exported ${currentRules.length} rule(s).`;
  statusEl.className = "status-text success-text";
}

function splitEscaped(line) {
  const fields = [];
  let current = "";
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === "\\" && i + 1 < line.length) {
      current += line[i + 1];
      i++;
    } else if (ch === "|") {
      fields.push(current);
      current = "";
    } else {
      current += ch;
    }
  }
  fields.push(current);
  return fields;
}

async function handleImport() {
  const adminKey = document.getElementById("admin-key").value;
  const statusEl = document.getElementById("import-export-status");
  const text = document.getElementById("import-export-text").value;
  const lines = text.split("\n").map((l) => l.trim()).filter((l) => l.length > 0);

  let imported = 0;
  for (const line of lines) {
    const fields = splitEscaped(line);
    if (fields.length !== 4) {
      statusEl.textContent = `Import failed on line "${line}": expected 4 fields, got ${fields.length}.`;
      statusEl.className = "status-text error-text";
      return;
    }

    const [name, isAllowText, precedenceText, pattern] = fields;
    const request = {
      name,
      isAllow: isAllowText === "true",
      precedence: Number(precedenceText),
      pattern,
      enabled: true,
    };

    try {
      await fetchJson("/api/admin/rules", adminKey, {
        method: "POST",
        body: JSON.stringify(request),
      });
      imported++;
    } catch (err) {
      statusEl.textContent = `Import failed on line "${line}": ${err.message} (${imported} rule(s) imported before this line).`;
      statusEl.className = "status-text error-text";
      await handleLoad();
      return;
    }
  }

  statusEl.textContent = `Imported ${imported} rule(s).`;
  statusEl.className = "status-text success-text";
  await handleLoad();
}

document.getElementById("load-rules").addEventListener("click", handleLoad);
document.getElementById("add-rule").addEventListener("click", handleAdd);
document.getElementById("run-test-rule").addEventListener("click", handleTest);
document.getElementById("export-rules").addEventListener("click", handleExport);
document.getElementById("import-rules").addEventListener("click", handleImport);
