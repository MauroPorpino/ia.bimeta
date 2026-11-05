import { cleanup } from '@testing-library/react';
import { afterEach, vi } from 'vitest';
import { reiniciarSessaoParaTeste } from '../api/session';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  reiniciarSessaoParaTeste();
});
