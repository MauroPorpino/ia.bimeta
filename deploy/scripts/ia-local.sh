#!/usr/bin/env bash
# O servico de IA local (Ollama) do Knowledge hibrido (ADR 0034): baixa os modelos uma vez, confere que respondem e mede o tempo de uma vetorizacao.
# Uso: ia-local.sh pull     baixa os modelos configurados (precisa de internet, so nesta etapa)
#      ia-local.sh check    confere o servico, a dimensao do vetor (1024) e a latencia de um embedding
# Roda com o servico "ollama" do perfil ia-local (docker compose --profile ia-local up -d ollama). Servico em OUTRA maquina: rode estes comandos nela (deploy/ollama/).
# NAO liga o modo semantico: isso e Knowledge__Semantic__Enabled (docs/operations/CONHECIMENTO-HIBRIDO.md), depois de conferida a qualidade.
set -uo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

embedding="${OLLAMA_EMBEDDING_MODEL:-qwen3-embedding:0.6b}"
geracao="${OLLAMA_GENERATION_MODEL:-}"

case "${1:-}" in
  pull)
    compose --profile ia-local exec -T ollama ollama pull "$embedding" || exit 1
    if [ -n "$geracao" ]; then compose --profile ia-local exec -T ollama ollama pull "$geracao" || exit 1; fi
    compose --profile ia-local exec -T ollama ollama list
    ;;
  check)
    inicio="$(date +%s%3N)"
    resposta="$(compose --profile ia-local exec -T ollama sh -c "curl -s http://127.0.0.1:11434/api/embed -d '{\"model\":\"$embedding\",\"input\":[\"Preciso de jejum para o exame de glicose?\"]}'" 2>/dev/null)" || { echo "O servico de IA local nao respondeu." >&2; exit 1; }
    fim="$(date +%s%3N)"
    dim="$(printf '%s' "$resposta" | tr -d ' \n' | sed -n 's/.*"embeddings":\[\[\([^]]*\)\]\].*/\1/p' | awk -F, '{print NF}')"
    echo "modelo: $embedding | dimensao: ${dim:-?} | latencia da 1a chamada: $((fim - inicio)) ms (inclui carregar o modelo)"
    if [ "${dim:-0}" != "1024" ]; then echo "ERRO: a dimensao deve ser 1024 (a coluna do indice e vector(1024))." >&2; exit 1; fi
    echo "ok"
    ;;
  *)
    echo "Uso: ia-local.sh pull|check" >&2
    exit 2
    ;;
esac
