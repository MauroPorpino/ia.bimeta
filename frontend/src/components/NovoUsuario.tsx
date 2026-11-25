import { useRef, useState, type FormEvent } from 'react';
import type { ApiClient } from '../api/client';
import type { AdmissionResult, CurrentUser, OrganizationSummary, PermissionCatalog } from '../api/types';
import { PERFIS, mensagemDeErroDeUsuario, organizacoesCom, perfilExigePermissaoDePerfil, pode, rotuloDoPerfil } from '../usuarios';
import { Dialogo } from './Dialogo';
import { PermissoesDoPerfil } from './UsoDeUsuarios';

interface Props {
  api: ApiClient;
  me: CurrentUser;
  organizacoes: OrganizationSummary[];
  catalogo: PermissionCatalog | null;
  onCriado: (resultado: AdmissionResult) => void;
  onCancelar: () => void;
}

/** Admissão: cria o usuário PENDENTE e devolve o token de primeiro acesso (que quem chama mostra uma única vez). */
export function NovoUsuario({ api, me, organizacoes, catalogo, onCriado, onCancelar }: Props) {
  const permitidas = organizacoesCom(me, 'UserManage', organizacoes);
  const [nome, setNome] = useState('');
  const [email, setEmail] = useState('');
  const [organizacao, setOrganizacao] = useState(permitidas[0]?.id ?? '');
  const [perfil, setPerfil] = useState<string>('Attendant');
  const [erro, setErro] = useState<string | null>(null);
  const [enviando, setEnviando] = useState(false);
  const emAndamento = useRef(false);

  const podeEscolherPerfilAlto = organizacao !== '' && pode(me, 'UserPermissionManage', organizacao);

  async function enviar(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current || organizacao === '') {
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      const resultado = await api.admitirUsuario(organizacao, { email: email.trim(), displayName: nome.trim(), role: perfil });
      onCriado(resultado);
    } catch (falha) {
      setErro(mensagemDeErroDeUsuario(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  return (
    <Dialogo titulo="Cadastrar usuário" onFechar={onCancelar}>
      <form className="revisao-formulario" onSubmit={(e) => void enviar(e)}>
        <label>
          Nome completo
          <input value={nome} onChange={(e) => setNome(e.target.value)} required maxLength={200} />
        </label>
        <label>
          E-mail
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required maxLength={320} autoComplete="off" />
        </label>
        <label>
          Organização
          <select value={organizacao} onChange={(e) => setOrganizacao(e.target.value)} required>
            {permitidas.length === 0 && <option value="">Nenhuma organização disponível</option>}
            {permitidas.map((o) => (
              <option key={o.id} value={o.id}>
                {o.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Perfil
          <select value={perfil} onChange={(e) => setPerfil(e.target.value)}>
            {PERFIS.map((p) => (
              <option key={p} value={p} disabled={perfilExigePermissaoDePerfil(p) && !podeEscolherPerfilAlto}>
                {rotuloDoPerfil(p)}
              </option>
            ))}
          </select>
        </label>
        <PermissoesDoPerfil catalogo={catalogo} perfil={perfil} />
        <p className="discreto">O usuário nasce pendente, ocupa uma vaga e recebe um token de primeiro acesso para definir a própria senha.</p>
        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        <div className="acoes">
          <button type="submit" className="btn btn-primario" disabled={enviando || organizacao === ''}>
            {enviando ? 'Cadastrando...' : 'Cadastrar'}
          </button>
          <button type="button" className="btn" onClick={onCancelar}>
            Cancelar
          </button>
        </div>
      </form>
    </Dialogo>
  );
}
