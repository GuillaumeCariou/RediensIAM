import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  base: '/admin/',
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      // The console is the SDK's first consumer: it resolves to the source in this repo rather
      // than a published build, so a change to the SDK is exercised by this app immediately.
      'rediensiam-web': path.resolve(__dirname, '../../sdk/typescript/rediensiam-web/src/index.ts'),
    },
  },
  build: {
    outDir: 'dist',
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
