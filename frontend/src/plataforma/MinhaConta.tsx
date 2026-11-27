import { useRef, useState, type FormEvent } from 'react';
import { SENHA_MAXIMA, SENHA_MINIMA, validarNovaSenha } from '../senha';
import { descreverErroPlataforma, type PlataformaApi } from './api';
import type { PlatformMe } from './types';

interface Props {
  api: PlataformaApi;
  me: PlatformMe;
  onSair: () => void;
}

export function MinhaConta({ api, me, onSair }: Props) {
  const [atual, setAtual] = useState('');
  const [nova, setNova] = useState('');
  const [confirmacao, setConfirmacao] = useState('');
  const [erro, setErro] = useState<string | null>(null);
  const [ok, setOk] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const emAndamento = useRef(false);

  async function trocar(e: FormEvent) {
    e.preventDefault();
    if (emAndamento.current) {
      return;
    }
    setOk(false);
    const problema = validarNovaSenha(nova, confirmacao, atual);
    if (problema) {
      setErro(problema);
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      await api.trocarSenha(atual, nova);
      setAtual('');
      setNova('');
      setConfirmacao('');
      setOk(true);
    } catch (f) {
      setErro(descreverErroPlataforma(f));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  return (
    <div className="pl-pagina">
      <h2>Minha conta</h2>
      <dl className="pl-dl">
        <dt>Nome</dt>
        <dd>{me.displayName}</dd>
        <dt>E-mail</dt>
        <dd>{me.email}</dd>
      </dl>

      <section aria-labelledby="pl-conta-senha">
        <h3 id="pl-conta-senha">Trocar a senha</h3>
        <form className="revisao-formulario pl-form-estreito" onSubmit={(e) => void trocar(e)}>
          <label>
            Senha atual
            <input type="password" value={atual} onChange={(e) => setAtual(e.target.value)} autoComplete="current-password" required />
          </label>
          <label>
            Nova senha
            <input type="password" value={nova} onChange={(e) => setNova(e.target.value)} autoComplete="new-password" required />
          </label>
          <label>
            Confirmar a nova senha
            <input type="password" value={confirmacao} onChange={(e) => setConfirmacao(e.target.value)} autoComplete="new-password" required />
          </label>
          <p className="discreto">
            A senha deve ter de {SENHA_MINIMA} a {SENHA_MAXIMA} caracteres.
          </p>
          {erro && (
            <p className="erro-campo" role="alert">
              {erro}
            </p>
          )}
          {ok && (
            <p className="aviso" role="status">
              Senha alterada.
            </p>
          )}
          <div className="acoes">
            <button type="submit" className="btn btn-primario" disabled={enviando}>
              Trocar senha
            </button>
          </div>
        </form>
      </section>

      <section aria-labelledby="pl-conta-sessao">
        <h3 id="pl-conta-sessao">Sessão</h3>
        <button type="button" className="btn" onClick={onSair}>
          Encerrar sessão
        </button>
      </section>

      <p className="discreto">A autenticação em dois fatores será exigida antes de qualquer exposição pública deste painel.</p>
    </div>
  );
}
