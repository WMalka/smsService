/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const api = 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/messages': api, '/health': api } },
  build: { outDir: 'dist' },
  test: { environment: 'jsdom', globals: true, setupFiles: './src/test/setup.ts' },
});
