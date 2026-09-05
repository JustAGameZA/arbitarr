// ESLint 9 flat config. The `lint` script runs with --max-warnings=0, so
// every rule enabled here is a hard CI failure, not advice.
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';

export default tseslint.config(
  {
    // Build output and deps are never linted. dist/ is Vite's default target and
    // ../Arbitarr.Host/wwwroot is this project's configured outDir, which holds
    // generated bundles plus (until Step 5b) the legacy static files.
    ignores: ['dist', 'node_modules', '../Arbitarr.Host/wwwroot'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      globals: { ...globals.browser },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // The admin key lives in a session-only Zustand store. A stray `any`
      // around it is how a typed secret quietly becomes an untyped one that
      // ends up in a URL or a log line, so this is an error, not a warning.
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  {
    // Config files and the test setup run in Node, not the browser.
    files: ['*.config.{js,ts}', 'vitest.setup.ts'],
    languageOptions: {
      globals: { ...globals.node },
    },
  },
);
