import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The factory host this app talks to. In development the Vite server proxies /api to it, so
// the browser sees one origin and the host's same-site session cookie works unchanged.
const host = process.env.FACTORY_HOST_URL ?? 'http://127.0.0.1:5180';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': { target: host, changeOrigin: false } },
  },
  build: {
    // The host serves the app from its wwwroot.
    outDir: '../Litos.SoftwareFactory.Host/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    restoreMocks: true,
  },
});
