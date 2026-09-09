'use strict';

/*
 * A deliberately tiny, dependency-free Torznab/Newznab upstream for the E2E golden path
 * (arb-rga.7). It exists so the E2E lane can prove Arbitarr's own search path end to end
 * without reaching any real indexer: no network egress, no credentials, no third-party
 * availability in the merge gate.
 *
 * PUBLIC REPO. Every fixture value here is invented. Titles are nonsense strings rather
 * than real release names, and there are no keys, tokens or real hostnames anywhere in
 * this file.
 *
 * ---------------------------------------------------------------------------------------
 * THE ONE THING THAT WILL BREAK THIS SILENTLY, IF IT IS EVER "TIDIED"
 * ---------------------------------------------------------------------------------------
 * Every <link> MUST be emitted on this server's own origin, i.e. exactly the base URL the
 * source row was created with (http://stub-upstream:5100). NzbHydraSource's SSRF guard
 * (TryValidateOriginPinnedLink, SEC-M1) compares scheme+host+port of each item's <link>
 * against the configured source origin and DROPS every item that differs -- silently, with
 * no error and no log line the test can see.
 *
 * So a link pointing at localhost, at 127.0.0.1, or at an example.invalid host does not
 * produce a failing assertion about links: it produces ZERO SEARCH ROWS and an E2E failure
 * that reads exactly like a broken product. That is why PUBLIC_ORIGIN is read from the
 * environment (set in compose.yml to the same value the source is created with) instead of
 * being hardcoded per-item, and why it must never be replaced with a *.invalid placeholder
 * "for the public repo" -- .invalid is correct for documentation, and fatal here.
 *
 * Related and equally load-bearing: SourceRepository.ValidateBaseUrl REJECTS any .invalid
 * host outright (arb-c29), so the source pointing at this stub cannot use one either.
 */

const http = require('node:http');

const PORT = Number(process.env.STUB_PORT || 5100);

// The origin this stub is reachable at from inside the compose network, and therefore the
// origin every <link> must carry. See the header comment -- this is not cosmetic.
const PUBLIC_ORIGIN = process.env.STUB_PUBLIC_ORIGIN || `http://stub-upstream:${PORT}`;

/*
 * Fixed search fixtures. Two items, one usenet and one torrent, so the E2E asserts a
 * protocol mapping that actually varies rather than one the parser could hardcode.
 * Invented titles: nothing here names a real release, group or indexer.
 */
const ITEMS = [
  {
    title: 'Example Show S01E01 1080p STUBGROUP',
    guid: 'stub-guid-0001',
    path: '/getnzb/stub-0001',
    size: 1073741824,
    category: 5000,
    protocol: 'usenet',
    pubDate: 'Mon, 08 Sep 2026 12:00:00 +0000',
  },
  {
    title: 'Example Show S01E02 1080p STUBGROUP',
    guid: 'stub-guid-0002',
    path: '/gettorrent/stub-0002',
    size: 2147483648,
    category: 5000,
    protocol: 'torrent',
    pubDate: 'Mon, 08 Sep 2026 13:00:00 +0000',
  },
];

function escapeXml(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/*
 * Caps. ParseCapsResponse reads category/subcat id+name, <searching> children's
 * available="yes" and supportedParams, and <limits max>. Kept to what the parser
 * actually consumes -- an unused element here is a claim nothing verifies.
 */
function capsXml() {
  return `<?xml version="1.0" encoding="UTF-8"?>
<caps>
  <server title="Stub Upstream" />
  <limits max="100" default="100" />
  <searching>
    <search available="yes" supportedParams="q" />
    <tv-search available="yes" supportedParams="q,tvdbid,season,ep" />
    <movie-search available="yes" supportedParams="q,tmdbid" />
  </searching>
  <categories>
    <category id="5000" name="TV">
      <subcat id="5040" name="TV/HD" />
    </category>
    <category id="2000" name="Movies" />
  </categories>
</caps>
`;
}

function searchXml() {
  const items = ITEMS.map(
    (item) => `    <item>
      <title>${escapeXml(item.title)}</title>
      <guid>${escapeXml(item.guid)}</guid>
      <link>${escapeXml(PUBLIC_ORIGIN + item.path)}</link>
      <pubDate>${escapeXml(item.pubDate)}</pubDate>
      <torznab:attr name="size" value="${item.size}" />
      <torznab:attr name="category" value="${item.category}" />
      <torznab:attr name="protocol" value="${item.protocol}" />
    </item>`,
  ).join('\n');

  return `<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
  <channel>
    <title>Stub Upstream</title>
${items}
  </channel>
</rss>
`;
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);
  const mode = url.searchParams.get('t');

  // A liveness probe for compose's healthcheck that is not itself a Torznab mode, so it
  // cannot be confused with the surface under test.
  if (url.pathname === '/ping') {
    res.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('ok');
    return;
  }

  /*
   * Both protocol paths are served. The source under test is pinned to Torznab
   * (torznab/api), but answering the Newznab path too means a protocol change in the test
   * does not turn into a mystifying 404 several layers away.
   */
  if (url.pathname !== '/torznab/api' && url.pathname !== '/api') {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('not found');
    return;
  }

  // Deliberately NOT checking the apikey. This stub authenticates nobody: it holds no
  // secret, and asserting on a key here would mean inventing one and putting it in a
  // public repo to no benefit. Arbitarr's key handling is covered by its own tests.
  if (mode === 'caps') {
    res.writeHead(200, { 'content-type': 'application/xml; charset=utf-8' });
    res.end(capsXml());
    return;
  }

  if (mode === 'search' || mode === 'tvsearch' || mode === 'movie') {
    res.writeHead(200, { 'content-type': 'application/xml; charset=utf-8' });
    res.end(searchXml());
    return;
  }

  res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8' });
  res.end('unsupported t= mode');
});

server.listen(PORT, '0.0.0.0', () => {
  // eslint-disable-next-line no-console
  console.log(`stub-upstream listening on ${PORT}, emitting links on ${PUBLIC_ORIGIN}`);
});
