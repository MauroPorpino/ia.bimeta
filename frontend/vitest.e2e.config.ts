import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// Testes FUNCIONAIS do editor contra a API REAL (e o PostgreSQL de teste dela). Só rodam por `npm run test:e2e`, com E2E_API_URL, E2E_TENANT, E2E_EMAIL, E2E_PASSWORD, E2E_ORG, E2E_UNIT e E2E_CHANNEL
// (o teste .NET `EditorDeFluxosComApiRealTests` sobe a API e define tudo isso). Ficam fora de `npm test`: dependem de infraestrutura.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/e2e/**/*.e2e-spec.{ts,tsx}'],
    css: false,
    testTimeout: 60_000,
    hookTimeout: 60_000,
  },
});
