import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { readFileSync } from 'node:fs';
import { mockApi } from './mock/api';

// Версия — из Directory.Build.props (одна на агент, окно и установщик), время сборки — момент npm run build.
const props = readFileSync(new URL('../../Directory.Build.props', import.meta.url), 'utf8');
const version = /<Version>([^<]+)<\/Version>/.exec(props)?.[1] ?? '0.0.0';

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
  define: {
    __APP_VERSION__: JSON.stringify(version),
    __BUILD_TIME__: JSON.stringify(Math.floor(Date.now() / 1000)),
  },
  base: './',
  build: {
    outDir: '../SysWatch.App/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1000,
  },
});
