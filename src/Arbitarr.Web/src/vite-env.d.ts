/// <reference types="vite/client" />

// Decision C1: one ambient wildcard declaration instead of generated per-file
// .css.d.ts. Zero build steps, nothing generated in the tree, and identical
// behaviour under `vite build`, `vitest` and `tsc --noEmit`.
//
// Accepted tradeoff: every class types as `string` through the index signature,
// so a typo like `styles.sidebarr` typechecks and yields undefined at runtime.
// Component tests (AC14) are the mitigation -- an undefined class collapses the
// styling those tests observe. Revisit if the stylesheet count exceeds ~30.
declare module '*.module.css' {
  const classes: { readonly [key: string]: string };
  export default classes;
}

// Vite's `?raw` suffix imports a file's contents as a string. Used by
// indexHtml.test.ts to assert on index.html without a Node fs dependency.
declare module '*.html?raw' {
  const content: string;
  export default content;
}
