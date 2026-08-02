import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    include: ['src/**/*.test.{ts,tsx}'],
    coverage: {
      // lcov because that is the format sonar.javascript.lcov.reportPaths reads. Without it the
      // scan warned "No coverage information will be saved" and the dashboard showed this SPA as
      // untested — the reporters defaulted to HTML and clover, neither of which it looks for.
      reporter: ['text-summary', 'lcov'],
      reportsDirectory: './coverage',
      // Every source file, not only the ones a test happens to import. Without this v8 reports a
      // percentage of what was loaded, so adding a file with no test at all *raises* the number —
      // the measurement flatters exactly the gap it should expose.
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/**/*.test.{ts,tsx}', 'src/test/**', 'src/vite-env.d.ts', 'src/main.tsx'],
    },
  },
})
