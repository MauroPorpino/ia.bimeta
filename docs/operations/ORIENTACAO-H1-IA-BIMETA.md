# Orientação objetiva para a etapa H1 (primeiros passos do especialista de infraestrutura)

**Documento E (complemento dos quatro principais).** Resume o que fazer **primeiro**, em ordem, para colocar a VM de homologação de pé. Detalhes e comandos: Manual B (`MANUAL-INSTALACAO-VM-IA-BIMETA.md`); critérios de aprovação: Checklist C, etapa H1. **Escopo desta etapa:** preparar a VM e provar que a pilha sobe em Docker real. **Fora de escopo agora:** credenciais definitivas, mensagens reais (WhatsApp), chaves de IA, clientes reais, IA local (Ollama), uso do modo Embedded com sistemas externos reais.

> Se qualquer passo falhar, **pare**, registre comando, saída exata e hora, e avise a equipe de desenvolvimento. Não altere arquivos de `deploy/` nem o código. **Nunca** cole segredos em chats, e-mails ou tickets.

## O que fazer primeiro (ordem)

1. **Receber e verificar o pacote** (Manual B §10): conferir o SHA-256 e o hash do commit do manifesto. Sem pacote conferido, não começar.
2. **Confirmar com o proprietário as informações de infraestrutura** da tabela "Indispensáveis" abaixo (domínio, IP fixo/NAT, cofre, VM) e registrar as respostas.
3. **Preparar a VM:** Ubuntu Server 24.04, 4 vCPU, 8 GB, 100 GB SSD (Manual B §1); atualizar (§2); usuário nominal e SSH por chave (§3). Fazer **snapshot** da VM limpa.
4. **DNS e portas:** registro A do domínio para o IP público; 80/443 de entrada até a VM; confirmar **de fora da rede** (Manual B §0 e §9).
5. **Descompactar o pacote em `/opt/iabimeta`** e rodar `sudo ./scripts/instalar-host.sh --ssh-de <REDE_ADMIN_CIDR>` (Manual B §11). Manter a sessão SSH aberta e testar um segundo acesso antes de fechá-la.
6. **Gerar os segredos de HOMOLOGAÇÃO** com `gerar-segredos.sh` (Manual B §12). Eles são **só desta VM de teste**: não são os definitivos de produção. **Copiar os três arquivos para o cofre fora da VM imediatamente.** Em `.env`, usar o certificado de **homologação** do Let's Encrypt (`ACME_CA` de staging).
7. **`preflight.sh`** (precisa sair sem ERRO) e **`subir.sh`** (aplica as migrations **em banco novo e vazio**, da própria VM de teste), conforme Manual B §13.
8. **`status.sh`** e **`teste-clamav.sh`**; conferir `https://<DOMINIO>/api/health` (Manual B §13 e §18).
9. **Teste de aceitação:** `sudo ./scripts/aceitacao.sh --confirmo-que-e-teste` (derruba contêineres de propósito; VM sem atendimento real). Todos os itens devem dar `PASSOU`. **Enviar o log integral ao desenvolvimento** (é a primeira prova real do Compose, dos Dockerfiles, do Caddy, do ClamAV e do systemd).
10. **Conferências de rede e segurança:** só 80/443 abertas (`nmap` de fora), IP real do cliente nos logs do `web`, reinício da VM (Checklist C, H1.12 a H1.14).
11. **Trocar para o certificado real** do Let's Encrypt quando os itens anteriores estiverem aprovados (H1.15).
12. **Entregar o registro do Checklist C (H1)** com as evidências. A etapa H2 (backup externo e restauração) só começa depois da aprovação de H1.

## Decisões, separadas por momento

### Indispensáveis para PREPARAR a VM (H1)
| Id | Decisão / informação | Quem | Obs. |
|---|---|---|---|
| P1 | Domínio de homologação e quem administra o DNS | Proprietário | |
| P2 | IP público fixo; responsável pelo NAT/firewall de borda; provedor não bloqueia 80/443 | Proprietário | |
| P4 | Cofre/gerenciador de senhas fora da VM e quem acessa | Proprietário | os segredos gerados são de teste |
| P5 | Autorização para gerar e entregar o pacote (hash, manifesto, SHA-256) | Proprietário | procedimento interno do desenvolvimento |
| P14 | Janela de manutenção e quem autoriza reinícios | Proprietário | |
| I1 | VM provisionada com o dimensionamento recomendado; snapshot | Infraestrutura | |
| I2 | Endurecimento do Ubuntu; decidir `fail2ban`, MFA e `unattended-upgrades` | Infraestrutura | não vêm no pacote |
| I5 | Confirmar que o IP real do cliente chega ao `web` | Infraestrutura | depende do NAT |

### Necessárias SOMENTE nas etapas posteriores
| Id | Decisão | Etapa |
|---|---|---|
| P3 | Destino da cópia externa de backup (e webhook de alerta opcional) | H2 |
| I8 | Teste de restauração em VM nova e tempo anotado | H2 |
| P13 | Se haverá sistema externo (Embedded) e qual | H3 |
| D1 | **CSP e `frame-ancestors`** tratados pelo desenvolvimento **antes** de qualquer homologação Embedded com sistema externo real | H3 |
| P6 | Conta Meta (app, WABA, número de teste, token, App Secret) e **autorização do primeiro envio real** | H4 |
| P7 | Chave de IA **de teste**, limite de gasto e orçamento | H5 |
| P11 | Custo máximo por atendimento e teto mensal | H8 |
| P12 | Quem acompanha o `status` e decide as "possíveis saídas" | piloto |
| P9, P10 | Parecer jurídico/LGPD, termos, política de privacidade, contrato | antes da produção/piloto |
| I4, I6, I7, I9, I10 | Fixar versões de imagem, monitoramento externo, rotação de logs, destino de produção, medição de desempenho | após H1, H7 e H8 |

### Já decididas
- **IA local (Ollama) NÃO entra nesta homologação** (`Knowledge__Semantic__Enabled=false`; não usar o perfil `ia-local`). O dimensionamento de 8 GB é suficiente sem ela.
- Mensagens reais, credenciais definitivas e clientes reais **não** fazem parte do H1.
- A lacuna de segurança **CSP e `frame-ancestors`** fica **registrada** (Inventário A, L1; Pendências D, D1) e será tratada **antes** da homologação Embedded com sistemas externos reais; não impede o H1.
