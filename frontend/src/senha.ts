/** A política de senha do servidor (12 a 128 caracteres): a tela confere antes de enviar, o servidor é quem decide. */
export const SENHA_MINIMA = 12;
export const SENHA_MAXIMA = 128;

/** O erro da nova senha (ou `null` se está ok). A senha atual, quando informada, não pode ser igual à nova. */
export function validarNovaSenha(nova: string, confirmacao: string, atual?: string): string | null {
  if (nova.length < SENHA_MINIMA) {
    return `A nova senha deve ter ao menos ${SENHA_MINIMA} caracteres.`;
  }
  if (nova.length > SENHA_MAXIMA) {
    return `A nova senha deve ter no máximo ${SENHA_MAXIMA} caracteres.`;
  }
  if (nova !== confirmacao) {
    return 'A confirmação não confere com a nova senha.';
  }
  if (atual !== undefined && nova === atual) {
    return 'A nova senha deve ser diferente da atual.';
  }
  return null;
}
