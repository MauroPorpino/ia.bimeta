import { useRef, useState, type FormEvent } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import { definirSessao } from '../api/session';

interface Props {
  api: ApiClient;
  /** Mensagem fixa acima do formulário (por exemplo, "sua sessão expirou"). */
  aviso?: string | null;
  onEntrou: () => void;
}

export function LoginScreen({ api, aviso, onEntrou }: Props) {
  const [tenant, setTenant] = useState('');
  const [email, setEmail] = useState('');
  const [senha, setSenha] = useState('');
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
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
        <label>
          E-mail
          <input
            name="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="username"
            required
          />
        </label>
        <label>
          Senha
          <input
            name="senha"
            type="password"
            value={senha}
            onChange={(e) => setSenha(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>

        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}

        <button type="submit" className="btn btn-primario" disabled={enviando}>
          {enviando ? 'Entrando...' : 'Entrar'}
        </button>
      </form>
    </main>
  );
}
