import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Local dev proxy targets the Host on loopback. 127.0.0.1 is deliberate: an
// RFC1918 address here would be committed homelab topology and the tree-wide
// guard in .github/workflows/build-test.yml would reject it.
const HOST_ORIGIN = 'http://127.0.0.1:8080';
const PROXIED_PREFIXES = ['/api', '/health', '/torznab', '/newznab', '/download'];

export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    // DEVELOPER CONVENIENCE ONLY. `npm run build` + `dotnet run` then serves the
    // SPA from the Host with no extra flags.
    //
    // This is NOT what ships. The Dockerfile passes --outDir /web/dist and that
    // value is authoritative: the build stage removes src/Arbitarr.Host/wwwroot
    // and copies /web/dist over it, so image content cannot depend on a
    // developer's local state. The two paths need NOT agree and no acceptance
    // criterion asserts that they do.
    //
    // Do not delete this for "one source of truth" -- `npm run build` would then
    // write Vite's default dist/, which .gitignore covers but `dotnet run` does
    // not serve, silently breaking the local loop.
    outDir: '../Arbitarr.Host/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: Object.fromEntries(
      PROXIED_PREFIXES.map((prefix) => [prefix, { target: HOST_ORIGIN, changeOrigin: true }]),
    ),
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./vitest.setup.ts'],
    // LOAD-BEARING, NOT COSMETIC. Vitest defaults to css: false, under which
    // `import './theme.css'` resolves to an empty stub and no stylesheet is ever
    // attached to the jsdom document. Every AC-CHROME assertion reads a resolved
    // custom property off getComputedStyle(document.documentElement); with no
    // stylesheet attached those reads return '' and the chrome gate is red on day
    // one -- a gate that cannot run looks exactly like a gate that is wrong, and
    // the tempting cure is to weaken it. With css: true the stylesheet is
    // processed and attached and the reads return the pinned literals.
    css: true,
  },
});
