import '@testing-library/jest-dom';

// Attach the pinned palette to the jsdom document for EVERY test file, not only
// the ones that happen to import it. This and `css: true` in vite.config.ts are
// the two halves of AC-CHROME's precondition and neither works alone: without
// css: true the import resolves to an empty stub, and without this import the
// stylesheet never reaches a document that a chrome test can read.
import './src/styles/theme.css';
