# Segurança dos arquivos de mídia (ADR 0032)

O que protege os arquivos que o cliente manda e que o atendente envia, como ligar cada proteção no ambiente do piloto e como operar. Vale para o Api, o Worker e o comando `attachments` (a MESMA configuração nos três; a pasta de arquivos é uma só).

## 1. O que está implementado

| Proteção | Como funciona | Chave de configuração |
|---|---|---|
| **Cota por empresa** | soma dos arquivos disponíveis (recebidos e enviados); exata sob concorrência; o que não cabe não é guardado | `Conversations:Attachments:TenantQuotaBytes` (padrão 5 GiB; `0` desliga) |
| **Verificação de código malicioso** | `clamd` (ClamAV) por TCP, protocolo `INSTREAM`; a amostra de teste EICAR é detectada sempre, sem daemon | `Conversations:Attachments:Scanner:{Mode,Host,Port,Timeout,Required}` |
| **Cifra em repouso** | AES-256-GCM, chave derivada por empresa; tenant, arquivo e chave autenticados | `Conversations:Attachments:Encryption:{ActiveKeyId,Keys:<id>,Required}` |
| **Integridade na abertura** | tamanho e SHA-256 conferidos contra a linha; nada alterado ou sumido é entregue | sempre ligada |
| **Prazo de guarda** | enviados 30 dias; recebidos **desligado até o proprietário decidir**; só o objeto é apagado e a trilha de auditoria registra antes | `Retention:Days:OutboundAttachments`, `Retention:Days:InboundAttachments` (padrão `0`) |
| **Conferência banco × pasta, varredura posterior, conversão para a cifra** | comando `attachments` | (sem chave: é comando) |
| **Backup e restauração com a pasta** | `Backup-Banco.ps1 -PastaDeAnexos`, `Restaurar-Anexos.ps1` | ver `BACKUP-E-RESTAURACAO.md` |

Ordem de segurança, em uma frase: com a verificação **exigida**, só o arquivo que o verificador declarou **limpo** abre, é enviado ao WhatsApp ou fica guardado; verificador fora do ar **nunca** libera nada.

## 2. Configuração do piloto

```json
{
  "Conversations": {
    "Attachments": {
      "RootPath": "/var/lib/iabimeta/anexos",
      "TenantQuotaBytes": 5368709120,
      "Scanner":    { "Mode": "ClamAv", "Host": "127.0.0.1", "Port": 3310, "Timeout": "00:00:30", "Required": true },
      "Encryption": { "ActiveKeyId": "k1", "Required": true }
    }
  }
}
```

**A chave de cifra nunca vai no arquivo de configuração do repositório.** Entra por variável de ambiente (o `__` separa os níveis; use identificador de chave sem hífen):

```text
Conversations__Attachments__Encryption__Keys__k1=<Base64 de 32 bytes>
```

Gerar uma chave (guarde-a num cofre, **fora** da máquina, junto da frase de cifra do backup):

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

```bash
openssl rand -base64 32
```

- **Perder a chave é perder os arquivos cifrados.** Restaurar em outro ambiente exige a **mesma** chave.
- **Rotação simples:** crie `k2`, ponha `ActiveKeyId = k2` e **mantenha** `k1` em `Keys` (os objetos antigos continuam abrindo com ela). Não há re-cifragem automática.
- `Scanner:Required = true` sem `Mode = ClamAv`, ou `Encryption:Required = true` sem chave ativa, **impede a partida** do Api e do Worker (a mensagem nunca repete uma chave).
- `Timeout` do verificador: de 1 segundo a 10 minutos.

## 3. O `clamd` (verificador)

**Na implantação em VM Linux (`IMPLANTACAO-LINUX.md`, ADR 0033) o ClamAV já vem na pilha** (imagem oficial `clamav/clamav`, LTS 1.4, assinaturas num volume e atualização automática), configurado como **exigido**, e `scripts/teste-clamav.sh` prova a integração real (benigno, 20 MB e a amostra EICAR) pelo comando `attachments test-scanner`. O que segue vale para quem instala o `clamd` por conta própria. Instalar e atualizar o ClamAV fora da pilha é **ação do operador do servidor**. Em Linux, pelos pacotes da distribuição (`clamav-daemon` e `clamav-freshclam` em Debian e Ubuntu), com estas decisões no `clamd.conf` (confira os nomes e padrões na página de manual da versão instalada):

- `TCPSocket 3310` e `TCPAddr 127.0.0.1`: o daemon **não tem autenticação**. Escute só no laço local ou numa rede privada, **nunca** na Internet; se o `clamd` roda em outra máquina, aponte `Scanner:Host` para ela e restrinja a porta por firewall.
- `StreamMaxLength` **maior ou igual ao maior limite de tipo** (padrão do ia.bimeta: documento de 20 MB; teto absoluto de 100 MB). Abaixo disso o daemon responde "INSTREAM size limit exceeded" e o arquivo grande fica indisponível (sempre sem liberar).
- Mantenha as assinaturas atualizadas (`freshclam` em serviço): verificador desatualizado é risco, e o `status` não sabe a idade das assinaturas.

Confirmar que o ia.bimeta enxerga o daemon:

```powershell
dotnet run --project backend/host/IaBimeta.Api --no-launch-profile -- attachments report
```

A linha "O verificador responde agora: sim" prova a conexão; a seção "Arquivos" do `Status-Ambiente.ps1` repete isso e fica **crítica** se o verificador é exigido e não responde. A disponibilidade do `clamd` no Windows não foi confirmada: o piloto deve usar Linux ou um daemon em outra máquina (decisão de hospedagem).

## 4. Comando `attachments`

Roda na máquina de quem opera, com a mesma configuração do Api. Só identificadores e números saem (nunca nome de arquivo, caminho nem chave). O que altera algo só age com `--confirm`; sem ele é simulação. Saída: `0` tudo certo, `1` há o que olhar, `2` uso inválido ou configuração que impede a operação.

| Comando | Para quê |
|---|---|
| `attachments report` | cota, cifra, verificador (e se responde) e o uso por empresa (arquivos, espaço, % da cota, recebidos, enviados, não verificados, indisponíveis, recusados) |
| `attachments test-scanner [--size-mb N]` | **prova o verificador REAL falando direto com o motor** (a amostra EICAR é interceptada pela detecção embutida em qualquer outro caminho): benigno pequeno limpo, benigno de N MB limpo (padrão 1; use 20 para provar o `StreamMaxLength`) e EICAR infectada. Sai 0 só se os três derem o esperado |
| `attachments check [--tenant ID]` | confere cada arquivo disponível contra a pasta: sem objeto, ilegível (chave ausente ou errada, adulterado), conteúdo diferente, órfãos e texto claro com a cifra ligada |
| `attachments check --repair --confirm [--orphan-days N] [--tenant ID]` | marca como `lost` a linha sem objeto e apaga o órfão mais antigo que N dias (padrão 1). **Nunca** altera o que não decifra ou não confere. A trilha de auditoria é gravada antes. **Trava:** se **todas** as linhas conferidas estão sem objeto e a pasta não tem **nenhum** objeto para o alcance pedido (quase sempre `RootPath` errado, ou pasta ainda não restaurada), o reparo **recusa** (saída 2, nada alterado); só a perda real se confirma com `--allow-empty-folder` |
| `attachments rescan [--confirm] [--tenant ID]` | verifica os arquivos guardados antes de o verificador existir: limpo vira verificado; infectado é bloqueado e apagado. **Necessário ao ligar `Required` onde já há arquivos** (eles não abrem até lá) |
| `attachments encrypt-existing [--confirm] [--tenant ID]` | converte os objetos antigos em texto claro para a cifra, no lugar, sem mudar o que abre. Idempotente |

`--tenant` recebe o identificador (GUID) da empresa e limita o alcance. Use-o no primeiro reparo.

## 5. O que o atendente e o operador veem

| Motivo gravado | Onde aparece | Significa | O que fazer |
|---|---|---|---|
| `quota-exceeded` | arquivo recebido indisponível; envio recusado (422) | a empresa passou da cota | liberar espaço (prazo de guarda, `report`) ou ampliar `TenantQuotaBytes`; pedir ao cliente que reenvie |
| `malware` | arquivo recusado (os dois sentidos) | o verificador achou código malicioso (ou a amostra EICAR) | nada (o arquivo não foi guardado) |
| `scan-unavailable` | arquivo recebido indisponível | o verificador exigido ficou fora do ar durante todas as tentativas | consertar o `clamd`; pedir ao cliente que reenvie |
| `scanner-unavailable` | envio do atendente recusado (503) | o verificador está fora do ar agora | repetir em instantes; nada foi guardado nem enviado |
| `retention` | arquivo indisponível, com nome e legenda | o prazo de guarda venceu e o objeto foi apagado | nada |
| `lost` | arquivo indisponível | a conferência não achou o objeto (disco, pasta restaurada sem ele) | restaurar a pasta de um backup, se existir; senão pedir ao cliente que reenvie |

## 6. Prazo de guarda e auditoria

- Enviados: 30 dias (ADR 0031). **Recebidos: `Retention:Days:InboundAttachments = 0` (nenhum apagamento) até o proprietário definir o prazo**; a recomendação técnica é 90 dias, a confirmar com a base legal e o setor do cliente (LGPD). Por empresa: `Retention:Tenants:<slug>:InboundAttachments`.
- Rode `retention report` antes de ligar o prazo dos recebidos pela primeira vez, **com um backup feito**: o expurgo do conteúdo é irreversível.
- Cada lote expurgado vai para a trilha de auditoria **antes** de ser apagado (operações `conversations.attachment.expired`, `.orphan-removed`, `.malware-blocked` e `.lost`; só identificadores, origem `retention`). Se a trilha estiver fora do ar, o expurgo para sem apagar nada.

## 7. Backup e restauração (resumo)

`Backup-Banco.ps1 -PastaDeAnexos <RootPath>` copia a pasta no mesmo backup, depois do dump, com manifesto de SHA-256; o backup agendado e a cópia externa cobrem os anexos; `Restaurar-Backup.ps1 -PastaDeAnexosDestino` e `Restaurar-Anexos.ps1` restauram só em pasta nova; `Ensaiar-Restauracao.ps1 -SimularPerdaDaOrigem` prova a recuperação sem a pasta original. **Depois de restaurar**, aponte `RootPath` para a pasta restaurada, use a **mesma chave** de cifra e rode `attachments check` (primeiro sem `--repair`). Detalhes em `BACKUP-E-RESTAURACAO.md`.

## 8. Checklist para ligar no ambiente do piloto

1. Banco de teste ou de homologação: **backup antes** de aplicar a migração `AttachmentScanStatus` (`Backup-Banco.ps1`).
2. `clamd` instalado, atualizado e escutando só onde deve; `StreamMaxLength` conferido; `attachments report` mostra "responde: sim".
3. Chave de cifra gerada, guardada **fora** da máquina e entregue por variável de ambiente ao Api, ao Worker e ao comando.
4. `Scanner:Required = true`, `Encryption:Required = true`, cota definida.
5. Se já havia arquivos: `attachments rescan` (simulação, depois `--confirm`) e `attachments encrypt-existing` (idem).
6. `Status-Ambiente.ps1` com a seção "Arquivos" em ordem; `attachments check` sem divergência.
7. Backup completo com `-PastaDeAnexos` e **um ensaio de restauração** em ambiente descartável.
8. Decisão do proprietário sobre o prazo de guarda dos recebidos registrada.

## 9. O que não está coberto

- O cliente do `clamd` **não foi exercitado contra um ClamAV real** (só contra um daemon falso que fala o protocolo): fazer na homologação (`attachments report` e um arquivo EICAR enviado pelo WhatsApp de teste).
- Sem re-cifragem de objetos com a chave nova, sem quarentena com interface, sem cota por usuário, sem prazo por tipo de arquivo.
- Desempenho com muitos objetos ou gigabytes não foi medido (conferência e backup crescem linearmente).
- Um antivírus do próprio servidor que ponha um objeto em quarentena (ou recuse a leitura) faz o arquivo aparecer como **ilegível** na conferência e como indisponível para o atendente: a leitura recusada nunca vira erro do servidor nem entrega conteúdo. Trate o objeto como suspeito (**não o restaure** da quarentena) e peça ao cliente que reenvie; o `check --repair` não o altera de propósito.
