import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { mockApi } from './mock/api';

// Сборка кладётся прямо в wwwroot окна SysWatch.App.
export default defineConfig({
  plugins: [
    react(),
    {
      name: 'syswatch-mock-api',
      configureServer: s => { s.middlewares.use(mockApi); },
      configurePreviewServer: s => { s.middlewares.use(mockApi); },
    },
  ],
  base: './',
  build: {
    outDir: '../SysWatch.App/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1000,
  },
});
