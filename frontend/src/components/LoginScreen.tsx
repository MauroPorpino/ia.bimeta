import { useRef, useState, type FormEvent } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import { definirSessao } from '../api/session';
import { validarNovaSenha } from '../senha';

interface Props {
  api: ApiClient;
  /** Mensagem fixa acima do formulário (por exemplo, "sua sessão expirou"). */
  aviso?: string | null;
  onEntrou: () => void;
}

type Modo = 'entrar' | 'redefinir';

export function LoginScreen({ api, aviso, onEntrou }: Props) {
  const [modo, setModo] = useState<Modo>('entrar');
  const [tenant, setTenant] = useState('');
  const [email, setEmail] = useState('');
  const [senha, setSenha] = useState('');
  const [token, setToken] = useState('');
  const [novaSenha, setNovaSenha] = useState('');
  const [confirmacao, setConfirmacao] = useState('');
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [aviso2, setAviso2] = useState<string | null>(null);
  const emAndamento = useRef(false);

  async function enviar(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current) {
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      const resposta = await api.login({ tenant: tenant.trim(), email: email.trim(), password: senha });
      definirSessao(resposta.token, resposta.expiresAt, tenant);
      setSenha('');
      onEntrou();
    } catch (falha) {
      setErro(descreverErro(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  async function redefinir(evento: FormEvent) {
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
      await api.redefinirSenha({ tenant: tenant.trim(), email: email.trim(), token: token.trim(), newPassword: novaSenha });
      // O token foi usado: nada dele fica na tela, e a pessoa entra com a senha nova.
      setToken('');
      setNovaSenha('');
      setConfirmacao('');
      setSenha('');
      setAviso2('Senha redefinida. Entre com a nova senha.');
      setModo('entrar');
    } catch (falha) {
      setErro(descreverErro(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  function alternar(proximo: Modo) {
    setErro(null);
    setAviso2(null);
    setModo(proximo);
  }

  const campoDaEmpresa = (
    <label>
      Tenant
      <input
        name="tenant"
        value={tenant}
        onChange={(e) => setTenant(e.target.value)}
        autoComplete="organization"
        autoCapitalize="none"
        spellCheck={false}
        placeholder="ex.: demo-a"
        required
      />
    </label>
  );
  const campoDoEmail = (
    <label>
      E-mail
      <input name="email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" required />
    </label>
  );

  if (modo === 'redefinir') {
    return (
      <main className="tela-entrada">
        <form className="cartao-entrada" onSubmit={(e) => void redefinir(e)} aria-labelledby="titulo-redefinir">
          <h1 id="titulo-redefinir">Redefinir a senha</h1>
          <p className="discreto">Use o token que o administrador da empresa (ou o operador) lhe entregou. Ele vale por pouco tempo e só uma vez.</p>

          {campoDaEmpresa}
          {campoDoEmail}
          <label>
            Token de redefinição
            <input name="token" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" autoCapitalize="none" spellCheck={false} placeholder="ibr_..." required />
          </label>
          <label>
            Nova senha
            <input name="nova-senha" type="password" value={novaSenha} onChange={(e) => setNovaSenha(e.target.value)} autoComplete="new-password" required />
          </label>
          <label>
            Confirmar a nova senha
            <input name="confirmacao" type="password" value={confirmacao} onChange={(e) => setConfirmacao(e.target.value)} autoComplete="new-password" required />
          </label>

          {erro && (
            <p className="erro-campo" role="alert">
              {erro}
            </p>
          )}

          <button type="submit" className="btn btn-primario" disabled={enviando}>
            {enviando ? 'Redefinindo...' : 'Redefinir senha'}
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
      <form className="cartao-entrada" onSubmit={(e) => void enviar(e)} aria-labelledby="titulo-entrada">
        <h1 id="titulo-entrada">Entrar no atendimento</h1>
        <p className="discreto">ia.bimeta - interface do atendente</p>

        {aviso && (
          <p className="aviso" role="status">
            {aviso}
          </p>
        )}
        {aviso2 && (
          <p className="aviso" role="status">
            {aviso2}
          </p>
        )}

        {campoDaEmpresa}
        {campoDoEmail}
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
        <button type="button" className="btn-link" onClick={() => alternar('redefinir')}>
          Esqueci a senha (tenho um token)
        </button>
      </form>
    </main>
  );
}
