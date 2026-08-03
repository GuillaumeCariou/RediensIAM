import { defineConfig } from 'vitest/config';
import { playwright } from '@vitest/browser-playwright';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  base: '/console/',
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
      // The console is the SDK's first consumer: it resolves to the source in this repo rather
      // than a published build, so a change to the SDK is exercised by this app immediately.
      'rediensiam-web': path.resolve(import.meta.dirname, '../../sdk/typescript/rediensiam-web/src/index.ts'),
    },
  },
  build: {
    outDir: 'dist',
  },
  /**
   * Two projects, because the suite has two genuinely different needs.
   *
   * `node` is for the tests that never render: two of them read this app's own source off disk
   * with `node:fs` and assert on its text, and one exercises `apiFetch` against a stubbed global
   * fetch. A DOM would be dead weight.
   *
   * `browser` is for the rendering tests. jsdom has no layout, no top layer and no inertness, so
   * a native `<dialog>` opened with showModal() could only ever be checked structurally there —
   * see `docs/TESTING.md`. Real Chromium gives `:modal`, real focus containment, a really inert
   * background and real event dispatch, which is what those tests are actually about.
   */
  test: {
    coverage: {
      // lcov because that is what sonar.javascript.lcov.reportPaths reads. Without it the scan
      // warned "No coverage information will be saved" and the dashboard showed this SPA as
      // untested — the default reporters are HTML and clover, neither of which it looks for.
      reporter: ['text-summary', 'lcov'],
      reportsDirectory: './coverage',
      // Every source file, not only the ones a test happens to import. Without this v8 reports a
      // percentage of what was loaded, so adding a file with no test at all *raises* the number —
      // the measurement flatters exactly the gap it should expose.
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/**/*.test.{ts,tsx}', 'src/test/**', 'src/vite-env.d.ts', 'src/main.tsx'],
    },
    projects: [
      {
        extends: true,
        test: {
          name: 'node',
          environment: 'node',
          // Every `.ts` test, by extension rather than by name: a test that needs a document is
          // written as `.tsx` and picked up by the browser project below. Listing the node files
          // individually meant a new one silently ran nowhere.
          include: ['src/**/*.test.ts'],
        },
      },
      {
        extends: true,
        test: {
          name: 'browser',
          include: ['src/**/*.test.tsx'],
          // Not shared with the node project: it installs DOM matchers and unmounts rendered
          // trees, neither of which means anything without a document.
          setupFiles: './src/test/setup.ts',
          browser: {
            enabled: true,
            headless: true,
            // A failing assertion is reported by the assertion, not by a PNG dropped into src/.
            screenshotFailures: false,
            provider: playwright(),
            instances: [{ browser: 'chromium' }],
          },
        },
      },
    ],
  },
});
