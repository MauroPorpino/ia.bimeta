import type { PermissionCatalog, UserUsage } from '../api/types';
import { agruparPorGrupo, permissoesPadraoDoPerfil, rotuloDoPerfil } from '../usuarios';

/** O limite é da BImeta: o tenant só vê o uso (nenhum controle altera limite ou habilitação). */
export function UsoDeUsuarios({ uso, erro }: { uso: UserUsage | null; erro: string | null }) {
  if (erro) {
    return (
      <p className="erro-campo" role="alert">
        {erro}
      </p>
    );
  }
  if (!uso) {
    return <p className="discreto">Carregando o uso de usuários...</p>;
  }
  const proximo = uso.limit !== null && uso.limit > 0 && uso.seatsUsed / uso.limit >= 0.8;
  const semVagas = uso.limit !== null && uso.available === 0;
  return (
    <section className="uso-usuarios" aria-label="Uso de usuários">
      <dl className="metricas">
        <div>
          <dt>Limite contratado</dt>
          <dd>{uso.limit === null ? 'Ilimitado' : uso.limit}</dd>
        </div>
        <div>
          <dt>Ativos</dt>
          <dd>{uso.active}</dd>
        </div>
        <div>
          <dt>Inativos</dt>
          <dd>{uso.inactive}</dd>
        </div>
        <div>
          <dt>Pendentes (reservam vaga)</dt>
          <dd>{uso.pending}</dd>
        </div>
        <div>
          <dt>Expirados</dt>
          <dd>{uso.expired}</dd>
        </div>
        <div>
          <dt>Vagas usadas</dt>
          <dd>{uso.seatsUsed}</dd>
        </div>
        <div>
          <dt>Vagas disponíveis</dt>
          <dd>{uso.available === null ? 'Ilimitadas' : uso.available}</dd>
        </div>
        <div>
          <dt>Cadastro de novos usuários</dt>
          <dd>{uso.registrationEnabled ? 'Habilitado' : 'Desabilitado'}</dd>
        </div>
      </dl>
      {!uso.registrationEnabled && (
        <p className="aviso" role="status">
          O cadastro de novos usuários está desabilitado pela BImeta.
        </p>
      )}
      {uso.overLimit && (
        <p className="aviso" role="status">
          Acima do limite: {uso.excess} {uso.excess === 1 ? 'usuário a mais' : 'usuários a mais'} do que o contratado. Solicite a ampliação do limite à BImeta.
        </p>
      )}
      {!uso.overLimit && semVagas && (
        <p className="aviso" role="status">
          Sem vagas disponíveis. Solicite a ampliação do limite à BImeta para cadastrar novos usuários.
        </p>
      )}
      {!uso.overLimit && !semVagas && proximo && (
        <p className="aviso" role="status">
          Próximo do limite: {uso.seatsUsed} de {uso.limit} vagas em uso.
        </p>
      )}
    </section>
  );
}

/** As permissões padrão de um perfil, agrupadas por grupo funcional (só as que estão no catálogo). */
export function PermissoesDoPerfil({ catalogo, perfil }: { catalogo: PermissionCatalog | null; perfil: string }) {
  if (!catalogo) {
    return null;
  }
  const grupos = agruparPorGrupo(permissoesPadraoDoPerfil(catalogo, perfil));
  return (
    <div className="perfil-permissoes" aria-label={`Permissões padrão do perfil ${rotuloDoPerfil(perfil)}`}>
      <p className="discreto">Permissões padrão do perfil {rotuloDoPerfil(perfil)}:</p>
      {grupos.length === 0 && <p className="discreto">Nenhuma permissão neste escopo.</p>}
      {grupos.map(({ grupo, itens }) => (
        <div key={grupo}>
          <strong>{grupo}</strong>
          <ul>
            {itens.map((p) => (
              <li key={p.permission}>{p.label}</li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  );
}
