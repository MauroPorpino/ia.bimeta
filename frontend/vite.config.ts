import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// A API do ia.bimeta roda em http://localhost:5155. O navegador só fala com o Vite (mesma origem):
// `/api/*` vira `http://localhost:5155/*` (o prefixo `/api` é removido), então não há CORS.
const API_ALVO = 'http://localhost:5155';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: API_ALVO,
        changeOrigin: true,
        rewrite: (caminho) => caminho.replace(/^\/api/, ''),
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
  },
});
