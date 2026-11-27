import { useRef, useState, type FormEvent } from 'react';
import { SENHA_MAXIMA, SENHA_MINIMA, validarNovaSenha } from '../senha';
import { descreverErroPlataforma, definirSessaoPlataforma, type PlataformaApi } from './api';

interface Props {
  api: PlataformaApi;
  aviso?: string | null;
  onEntrou: () => void;
}

type Modo = 'entrar' | 'primeiro-acesso';

/** Entrada do painel da plataforma: e-mail e senha, ou "Primeiro acesso" com o token de uso único. */
export function PlataformaLogin({ api, aviso, onEntrou }: Props) {
  const [modo, setModo] = useState<Modo>('entrar');
  const [email, setEmail] = useState('');
  const [senha, setSenha] = useState('');
  const [token, setToken] = useState('');
  const [novaSenha, setNovaSenha] = useState('');
  const [confirmacao, setConfirmacao] = useState('');
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const emAndamento = useRef(false);

  async function entrar(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      const resposta = await api.login(email.trim(), senha);
      definirSessaoPlataforma(resposta.token, resposta.expiresAt);
      setSenha('');
      onEntrou();
    } catch (falha) {
      setErro(descreverErroPlataforma(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  async function primeiroAcesso(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current) {
      return;
    }
    const problema = validarNovaSenha(novaSenha, confirmacao);
    if (problema) {
      setErro(problema);
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      await api.redimirPrimeiroAcesso(email.trim(), token.trim(), novaSenha);
      setToken('');
      setNovaSenha('');
      setConfirmacao('');
      setSenha('');
      setInfo('Senha definida. Entre com a nova senha.');
      setModo('entrar');
    } catch (falha) {
      setErro(descreverErroPlataforma(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  function alternar(proximo: Modo) {
    setErro(null);
    setInfo(null);
    setModo(proximo);
  }

  const campoEmail = (
    <label>
      E-mail
      <input name="email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" required />
    </label>
  );

  if (modo === 'primeiro-acesso') {
    return (
      <main className="tela-entrada">
        <form className="cartao-entrada" onSubmit={(e) => void primeiroAcesso(e)} aria-labelledby="pl-titulo-primeiro">
          <p className="pl-marca">Plataforma BImeta</p>
          <h1 id="pl-titulo-primeiro">Primeiro acesso</h1>
          <p className="discreto">Use o token de uso único que lhe foi entregue. Ele vale por pouco tempo e só uma vez.</p>
          {campoEmail}
          <label>
            Token de primeiro acesso
            <input name="token" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" autoCapitalize="none" spellCheck={false} required />
          </label>
          <label>
            Nova senha
            <input name="nova-senha" type="password" value={novaSenha} onChange={(e) => setNovaSenha(e.target.value)} autoComplete="new-password" required />
          </label>
          <label>
            Confirmar a nova senha
            <input name="confirmacao" type="password" value={confirmacao} onChange={(e) => setConfirmacao(e.target.value)} autoComplete="new-password" required />
          </label>
          <p className="discreto">
            A senha deve ter de {SENHA_MINIMA} a {SENHA_MAXIMA} caracteres.
          </p>
          {erro && (
            <p className="erro-campo" role="alert">
              {erro}
            </p>
          )}
          <button type="submit" className="btn btn-primario" disabled={enviando}>
            {enviando ? 'Definindo...' : 'Definir senha'}
          </button>
          <button type="button" className="btn-link" onClick={() => alternar('entrar')}>
            Voltar para a entrada
          </button>
        </form>
      </main>
    );
  }

  return (
    <main className="tela-entrada">
      <form className="cartao-entrada" onSubmit={(e) => void entrar(e)} aria-labelledby="pl-titulo-entrada">
        <p className="pl-marca">Plataforma BImeta</p>
        <h1 id="pl-titulo-entrada">Entrar na plataforma</h1>
        <p className="discreto">ia.bimeta - administração da plataforma</p>
        {aviso && (
          <p className="aviso" role="status">
            {aviso}
          </p>
        )}
        {info && (
          <p className="aviso" role="status">
            {info}
          </p>
        )}
        {campoEmail}
        <label>
          Senha
          <input name="senha" type="password" value={senha} onChange={(e) => setSenha(e.target.value)} autoComplete="current-password" required />
        </label>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <button type="submit" className="btn btn-primario" disabled={enviando}>
          {enviando ? 'Entrando...' : 'Entrar'}
        </button>
        <button type="button" className="btn-link" onClick={() => alternar('primeiro-acesso')}>
          Primeiro acesso (tenho um token)
        </button>
      </form>
    </main>
  );
}
