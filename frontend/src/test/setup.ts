import { cleanup, configure } from '@testing-library/react';
import { afterEach, vi } from 'vitest';
import { reiniciarSessaoParaTeste } from '../api/session';

// O padrão de 1 s do waitFor é curto para a primeira execução, com o cache frio e a máquina ocupada: o teste passa a esperar até 5 s antes de reprovar.
configure({ asyncUtilTimeout: 5000 });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  reiniciarSessaoParaTeste();
});
