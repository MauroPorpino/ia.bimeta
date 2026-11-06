import { useRef, useState, type FormEvent } from 'react';
import { descreverErro, type ApiClient } from '../api/client';
import { validarNovaSenha } from '../senha';

interface Props {
  api: ApiClient;
  onFechar: () => void;
}

/** A troca da PRÓPRIA senha: exige a atual; as outras sessões da pessoa caem e esta continua. */
export function TrocarSenha({ api, onFechar }: Props) {
  const [atual, setAtual] = useState('');
  const [nova, setNova] = useState('');
  const [confirmacao, setConfirmacao] = useState('');
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [feito, setFeito] = useState(false);
  const emAndamento = useRef(false);

  async function enviar(evento: FormEvent) {
    evento.preventDefault();
    if (emAndamento.current) {
      return;
    }
    const problema = validarNovaSenha(nova, confirmacao, atual);
    if (problema) {
      setErro(problema);
      return;
    }
    emAndamento.current = true;
    setEnviando(true);
    setErro(null);
    try {
      await api.trocarSenha({ currentPassword: atual, newPassword: nova });
      setFeito(true);
      setAtual('');
      setNova('');
      setConfirmacao('');
    } catch (falha) {
      setErro(descreverErro(falha));
    } finally {
      emAndamento.current = false;
      setEnviando(false);
    }
  }

  return (
    <section className="painel-senha" aria-labelledby="titulo-trocar-senha">
      <form onSubmit={(e) => void enviar(e)}>
        <h2 id="titulo-trocar-senha">Trocar minha senha</h2>
        <label>
          Senha atual
          <input type="password" name="senha-atual" value={atual} onChange={(e) => setAtual(e.target.value)} autoComplete="current-password" required />
        </label>
        <label>
          Nova senha
          <input type="password" name="nova-senha" value={nova} onChange={(e) => setNova(e.target.value)} autoComplete="new-password" required />
        </label>
        <label>
          Confirmar a nova senha
          <input type="password" name="confirmacao" value={confirmacao} onChange={(e) => setConfirmacao(e.target.value)} autoComplete="new-password" required />
        </label>
        <p className="discreto">De 12 a 128 caracteres. As suas outras sessões serão encerradas.</p>

        {erro && (
          <p className="erro-campo" role="alert">
            {erro}
          </p>
        )}
        {feito && (
          <p className="aviso" role="status">
            Senha alterada. As outras sessões foram encerradas.
          </p>
        )}

        <div className="acoes-senha">
          <button type="submit" className="btn btn-primario" disabled={enviando}>
            {enviando ? 'Salvando...' : 'Salvar nova senha'}
          </button>
          <button type="button" className="btn" onClick={onFechar}>
            Fechar
          </button>
        </div>
      </form>
    </section>
  );
}
