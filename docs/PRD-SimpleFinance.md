# PRD — SimpleFinance
_v0.1 · 2026-07-09 · rascunho gerado com prd-wizard_

Sistema pessoal de finanças que conecta às contas bancárias, cartões e investimentos do usuário via **Pluggy (Open Finance Brasil)**, consolida tudo numa visão única e ajuda a entender gastos, seguir orçamento e acompanhar patrimônio. **Uso estritamente pessoal, um único usuário, auto-hospedado.**

---

## 1. Visão geral

### 1.1 Nome
SimpleFinance

### 1.2 Descrição
O SimpleFinance é um app web pessoal de finanças que se conecta às instituições financeiras do usuário através da API da Pluggy (agregador de Open Finance Brasil). Ele importa automaticamente contas, cartões de crédito, extratos e investimentos, categoriza as transações (usando a categoria da Pluggy + regras do próprio usuário), e apresenta dashboard consolidado, orçamento por categoria, detecção de assinaturas recorrentes, metas de economia, relatórios e alertas. Não há entrada manual de transações — a fonte de verdade é o que os bancos retornam via Pluggy — mas o usuário pode **recategorizar** e criar **regras**.

### 1.3 Stack (em bullets)
- **Backend:** .NET 10 (ASP.NET Core, Minimal API/Controllers)
- **Frontend:** React + Vite + TypeScript
- **Banco:** PostgreSQL
- **ORM:** EF Core (CRUD/domínio) + Dapper (relatórios/consultas agregadas)
- **Integração financeira:** Pluggy (Connect Widget + REST API + Webhooks)
- **Jobs agendados:** Hangfire (sync diário, snapshots de patrimônio, detecção de recorrentes)
- **Arquitetura:** monólito modular por domínio (API / Application / Domain / Infrastructure), **single-user** (sem multi-tenant)
- **Deploy:** Docker Compose em VPS (api + front + postgres)
- **Auth:** usuário único, senha (hash Argon2/BCrypt) + 2FA TOTP, sessão JWT
- **Notificações:** Telegram Bot (principal) + e-mail SMTP (opcional)

## 2. Objetivo & métricas de sucesso

### 2.1 Objetivo principal
Dar ao usuário, em um só lugar e sem digitar nada, uma resposta clara para:
- Quanto eu tenho no total (patrimônio líquido consolidado)?
- Para onde foi meu dinheiro este mês (gastos por categoria)?
- Estou dentro do orçamento que planejei?
- Quais assinaturas/recorrências estão drenando minha conta?
- Como meu patrimônio evolui ao longo do tempo?
- Estou perto das minhas metas de economia?

### 2.2 Proposta de valor
Deve ser percebido como:
- **Automático** — conecta o banco uma vez e os dados chegam sozinhos.
- **Consolidado** — todos os bancos/cartões/investimentos numa tela.
- **Privado** — roda na infraestrutura do próprio usuário, dados não vão para terceiros (além da Pluggy, que é o agregador).
- **Sem fricção** — nada de planilha, nada de lançamento manual.

### 2.3 Diferencial principal
Diferente de Mobills/Organizze (SaaS com dados na nuvem do fornecedor e recursos de sincronização atrás de paywall), o SimpleFinance é **auto-hospedado e de dono único**: o usuário controla o banco de dados, as credenciais Pluggy e o servidor. É "meu Open Finance, minhas regras", sem mensalidade de app e sem compartilhar histórico financeiro com uma empresa de software.

### 2.4 Métricas de sucesso
- **North Star:** % de transações do mês corretamente categorizadas sem intervenção manual (alvo: ≥ 85% após 2 meses de regras ajustadas).
- **Apoio 1:** frescor dos dados — nenhuma conexão com sync > 24h de atraso (alvo: 100% via webhook + job diário).
- **Apoio 2:** cobertura — % do patrimônio real do usuário refletido no app (alvo: todas as instituições suportadas pela Pluggy conectadas).
- **Apoio 3:** tempo pra responder "para onde foi meu dinheiro?" — abrir dashboard e ver top categorias em < 3s.

## 3. Público-alvo

### 3.1 Cliente ideal
Uma única pessoa: o próprio dono/desenvolvedor. Perfil técnico o suficiente para rodar Docker numa VPS, com contas em bancos brasileiros suportados pela Pluggy, que quer controle financeiro automático sem confiar dados a um app comercial.

### 3.2 Usuários/papéis
- **Usuário único (owner)** — acesso total. Não há RBAC, não há convites, não há outros papéis.

### 3.3 Quem paga vs quem usa
Mesma pessoa. Custo Pluggy no **tier gratuito para uso pessoal** (ver §19 e §7.7).

## 4. Modelo de negócio
Não se aplica — não é um produto comercial. É ferramenta pessoal auto-hospedada, sem planos, sem cobrança, sem clientes. Único "custo" externo é a Pluggy (tier gratuito pessoal no MVP).

## 5. Escopo do MVP

### 5.1 Módulos incluídos no MVP
1. Autenticação single-user (senha + 2FA TOTP + JWT)
2. Integração Pluggy — Connect Widget, conexões (items), sync
3. Sincronização automática (webhook + job diário) + botão manual
4. Contas e cartões de crédito (saldos, limites, faturas)
5. Transações unificadas (extrato consolidado, busca, filtro)
6. Categorias + regras de categorização + recategorização manual
7. Dashboard consolidado
8. Orçamento por categoria (tetos mensais, gasto vs. planejado)
9. Investimentos (posições consolidadas)
10. Patrimônio líquido + snapshots + evolução no tempo
11. Detecção de recorrentes/assinaturas
12. Metas de economia
13. Relatórios + exportação CSV/Excel
14. Alertas/notificações (Telegram + e-mail)
15. Configurações (conexões, categorias, regras, alertas, backup)
16. Tema claro/escuro + layout responsivo

### 5.2 Fora do MVP
- Multiusuário / compartilhamento familiar / RBAC
- Multi-tenant / SaaS / cobrança
- Entrada manual de transações (dinheiro em espécie, etc.)
- Pagamentos/iniciação de pagamento (Pluggy Payments)
- App mobile nativo (será web responsiva)
- Previsão/projeção de fluxo de caixa com IA
- Conciliação de nota fiscal / imposto de renda
- Categorização por IA/LLM (regras determinísticas no MVP)
- Split/divisão de uma transação em várias categorias
- Multi-moeda real (assume BRL; guarda CurrencyCode mas não converte)

### 5.3 Arquitetura prepara para depois
Mesmo fora do MVP, o design deve deixar espaço para:
- **Camada de abstração do provedor** (interface `IFinancialDataProvider`) para eventual troca/adição de Belvo/Tecnospeed sem reescrever o domínio
- Multiusuário (todas as tabelas já carregam `UserId`, mesmo com 1 usuário)
- Split de transação (tabela `TransactionSplit` prevista, não implementada)
- Categorização por IA (o motor de regras é plugável)
- Pluggy Payments / iniciação de pagamento
- Multi-moeda com conversão

## 6. Requisitos funcionais

### 6.1 Autenticação
RF-AUTH-001 — Bootstrap do usuário único
No primeiro start, se não existir usuário, o sistema exibe tela de setup para criar o único usuário: nome, e-mail, senha. Após criado, o endpoint de setup é bloqueado.
Aceite: só é possível criar 1 usuário; segunda tentativa retorna 409/redireciona pro login.

RF-AUTH-002 — Login
Login com e-mail + senha. Senha verificada contra hash (Argon2id ou BCrypt). Emite JWT (access + refresh).

RF-AUTH-003 — 2FA TOTP
Após ativar, o login exige código TOTP (6 dígitos, janela 30s) de um app autenticador. Ativação gera secret + QR code + códigos de recuperação (8 códigos de uso único).
Aceite: com 2FA ativo, login sem código válido é recusado; código de recuperação consome 1 uso.

RF-AUTH-004 — Sessão e logout
Refresh token rotativo, expiração configurável. Logout invalida o refresh token atual.

RF-AUTH-005 — Troca de senha
Usuário pode trocar a senha informando a atual + nova. Invalida sessões existentes (opcional).

### 6.2 Integração Pluggy — Conexões
RF-PLG-001 — Credenciais e API Key
O backend guarda `PLUGGY_CLIENT_ID` e `PLUGGY_CLIENT_SECRET` via variáveis de ambiente. Antes de cada operação, obtém/renova o **API Key** da Pluggy (`POST /auth`, válido ~2h) e o cacheia.
Aceite: chamadas à API Pluggy usam API Key válido; renova automaticamente ao expirar.

RF-PLG-002 — Connect Token
Para abrir o Connect Widget, o backend gera um **Connect Token** (`POST /connect_token`) e devolve ao front. O front instancia o Pluggy Connect Widget com esse token.
Aceite: o widget abre e lista os conectores/instituições disponíveis.

RF-PLG-003 — Conectar uma instituição (criar Item)
No widget, o usuário escolhe a instituição e informa as credenciais dela (fica na Pluggy, nunca no SimpleFinance). Ao concluir, o widget retorna um `itemId`. O backend persiste uma **Connection** (nosso wrapper do Item Pluggy) e dispara o primeiro sync.
Aceite: após conectar, a conexão aparece em Configurações → Conexões com status e instituição.

RF-PLG-004 — Estados da conexão (Item)
A conexão espelha o status do Item Pluggy:
- `UPDATING` — sincronizando
- `UPDATED` — dados atualizados
- `LOGIN_ERROR` — credenciais inválidas/expiradas (requer reconexão)
- `WAITING_USER_INPUT` — precisa de MFA/input do usuário
- `OUTDATED` — falhou última atualização
- `MERGE_ERROR` / erro genérico
Aceite: cada estado é exibido com ação sugerida (ex.: `LOGIN_ERROR` → botão "Reconectar" que reabre o widget em modo update).

RF-PLG-005 — Reconectar / atualizar credenciais
Quando um Item está em `LOGIN_ERROR`/`WAITING_USER_INPUT`, o usuário reabre o widget em modo update (Connect Token com `itemId`) para reautenticar.

RF-PLG-006 — Remover conexão
Remover uma conexão: chama `DELETE /items/{id}` na Pluggy e faz soft delete local das contas/transações/investimentos vinculados (ou marca como arquivados). Pede confirmação.

RF-PLG-007 — Sandbox first
No MVP, o sistema funciona em ambiente **sandbox** da Pluggy por padrão, com flag de ambiente (`PLUGGY_ENV=sandbox|production`). Conectores sandbox permitem testar todo o fluxo sem custo/dados reais.

### 6.3 Sincronização
RF-SYNC-001 — Webhook receiver
Endpoint público `POST /api/webhooks/pluggy` recebe eventos: `item/created`, `item/updated`, `item/error`, `transactions/created`, `transactions/updated`, `transactions/deleted`, `connector/status_updated`. Valida a origem (segredo/assinatura configurada na Pluggy). Persiste o evento bruto em `WebhookEvent` e enfileira o processamento.
Aceite: ao receber `item/updated`, o sistema busca dados novos daquele item e atualiza contas/transações/investimentos.

RF-SYNC-002 — Job diário
Job Hangfire agendado (ex.: 06:00 e 18:00, configurável) percorre todas as conexões ativas, dispara atualização na Pluggy (`PATCH /items/{id}` quando aplicável) e reconcilia os dados. Garante frescor mesmo se um webhook falhar.

RF-SYNC-003 — Sync manual
Botão "Atualizar agora" por conexão e global. Enfileira sync imediato. Mostra progresso/estado.

RF-SYNC-004 — Reconciliação idempotente
O sync é idempotente: usa `PluggyTransactionId`/`PluggyAccountId`/`PluggyInvestmentId` como chave. Insere novos, atualiza alterados (ex.: `PENDING`→`POSTED`), marca deletados. Nunca duplica.
Aceite: rodar o sync 2x seguidas não cria transações duplicadas.

RF-SYNC-005 — Log de sync
Cada execução registra `SyncLog`: conexão, gatilho (WEBHOOK/CRON/MANUAL), início/fim, nº de transações adicionadas/atualizadas, status, erro.

RF-SYNC-006 — Aplicar regras no sync
Ao inserir transações novas, o motor de regras (RF-CAT-003) roda automaticamente para atribuir a categoria do usuário.

### 6.4 Contas e Cartões
RF-ACC-001 — Contas bancárias
Cada conta importada da Pluggy guarda: tipo (BANK), subtipo (CHECKING_ACCOUNT/SAVINGS_ACCOUNT), nome, número mascarado, saldo, moeda, instituição, marketing name.

RF-ACC-002 — Cartões de crédito
Contas tipo CREDIT guardam dados do cartão: limite, crédito disponível, saldo da fatura, data de fechamento, data de vencimento, bandeira (brand), nível (level), últimos dígitos.
Aceite: a tela de cartões mostra fatura atual, limite usado/disponível e vencimento.

RF-ACC-003 — Saldo consolidado
O sistema soma saldos de contas (ativo) e dívidas de cartões (passivo) para compor o patrimônio (§6.9).

RF-ACC-004 — Ativar/ocultar conta
O usuário pode ocultar uma conta específica dos totais/dashboard sem removê-la (flag `IsHidden`).

### 6.5 Transações
RF-TX-001 — Extrato unificado
Lista consolidada de todas as transações de todas as contas/cartões, ordenável por data, com paginação/scroll infinito.

RF-TX-002 — Campos da transação
Cada transação guarda: data, descrição (tratada e raw), valor, tipo (DEBIT/CREDIT), moeda, saldo pós-transação (quando disponível), status (POSTED/PENDING), merchant (nome/cnpj quando houver), categoria Pluggy, categoria do usuário (override), conta de origem, dados de pagamento (json), notas do usuário.

RF-TX-003 — Filtros e busca
Filtrar por: período, conta/cartão, categoria, tipo (entrada/saída), status, faixa de valor, texto (descrição/merchant). Busca rápida por texto.

RF-TX-004 — Recategorizar
O usuário pode trocar a categoria de uma transação manualmente. Opção "aplicar a todas parecidas" que sugere criar uma regra (RF-CAT-003).
Aceite: recategorizar reflete imediatamente em dashboard e orçamento.

RF-TX-005 — Notas e ocultar
Adicionar nota livre a uma transação; ocultar transação de relatórios (ex.: transferência entre contas próprias) via flag `IsHidden`.

RF-TX-006 — Detectar transferências internas
Heurística para marcar transferências entre contas do próprio usuário (mesmo valor, contas próprias, datas próximas) como `TRANSFER`, para não contarem como despesa/receita.
Aceite: mover R$ dentro das próprias contas não infla gastos do mês.

### 6.6 Categorias e Regras
RF-CAT-001 — Árvore de categorias
Categorias com hierarquia (categoria → subcategoria), tipo (EXPENSE/INCOME/TRANSFER), ícone e cor. Seed inicial baseado no padrão de categorias da Pluggy (Alimentação, Transporte, Moradia, Saúde, Lazer, Educação, Compras, Serviços, Renda, Investimentos, Transferências, etc.).

RF-CAT-002 — Mapeamento Pluggy→usuário
Cada categoria da Pluggy tem um mapeamento default para uma categoria do sistema. Transação nova recebe a categoria mapeada, salvo se uma regra do usuário sobrescrever.

RF-CAT-003 — Motor de regras
Regras do usuário com: campo de match (DESCRIPTION / MERCHANT / PLUGGY_CATEGORY / AMOUNT), operador (CONTAINS / EQUALS / REGEX / RANGE), valor, categoria-alvo, prioridade, ativo/inativo. Regras rodam em ordem de prioridade no sync e ao recategorizar em massa.
Aceite: regra "descrição contém 'IFOOD' → Alimentação" recategoriza automaticamente novas transações do iFood.

RF-CAT-004 — Recategorização em massa
Aplicar uma regra/categoria retroativamente a todas as transações que casam.

RF-CAT-005 — CRUD de categorias
Criar, editar, arquivar categorias e subcategorias. Categorias de sistema não podem ser excluídas, só ocultadas.

### 6.7 Dashboard
RF-DASH-001 — Visão consolidada
Dashboard inicial mostra: patrimônio líquido atual, saldo total em contas, dívida total de cartões, receita e despesa do mês corrente, saldo do mês (receita−despesa).

RF-DASH-002 — Gastos por categoria
Gráfico (pizza/barra) de despesas do mês por categoria, com top 5 e link para o extrato filtrado.

RF-DASH-003 — Evolução
Gráfico de linha: patrimônio líquido nos últimos 6/12 meses (a partir dos snapshots, §6.9). Gráfico de receita vs. despesa por mês.

RF-DASH-004 — Orçamento resumido
Cartões dos orçamentos do mês com barra de progresso (gasto/limite) e destaque dos estourados.

RF-DASH-005 — Próximos vencimentos
Faturas de cartão a vencer e recorrentes esperadas nos próximos dias.

RF-DASH-006 — Seletor de período
Alternar o dashboard entre mês atual, mês anterior e intervalo custom.

### 6.8 Orçamento
RF-BUD-001 — Definir orçamento
Definir teto mensal por categoria (ex.: Alimentação R$ 1.500). Orçamentos valem por mês; o sistema replica o do mês anterior por padrão, permitindo ajuste.

RF-BUD-002 — Acompanhamento
Para cada categoria orçada: gasto atual do mês, % do teto, valor restante, projeção de fim de mês (com base no ritmo).

RF-BUD-003 — Estados
Estados do orçamento: OK (< 80%), Atenção (80–100%), Estourado (> 100%). Cor/badge conforme estado.

RF-BUD-004 — Alerta de estouro
Ao cruzar limiares (ex.: 90% e 100%), dispara notificação (§6.13).

RF-BUD-005 — Orçamento total
Além de por categoria, um teto global de despesa mensal opcional.

### 6.9 Investimentos & Patrimônio
RF-INV-001 — Posições de investimento
Importar investimentos da Pluggy: tipo (MUTUAL_FUND, FIXED_INCOME, EQUITY, ETF, SECURITY, COE, CRYPTO), subtipo, nome, instituição/emissor, saldo/valor atual, quantidade, valor aplicado, taxa (mensal/anual quando disponível), datas (aplicação/vencimento).

RF-INV-002 — Consolidação
Tela de investimentos com total investido, distribuição por tipo/emissor (gráfico), e lista detalhada.

RF-INV-003 — Patrimônio líquido
Patrimônio = (saldos de contas + valor de investimentos) − (dívidas de cartão + outros passivos). Calculado sob demanda e persistido em snapshot diário.

RF-INV-004 — Snapshot diário
Job diário grava `NetWorthSnapshot`: ativos, passivos, patrimônio, quebrado por classe (contas, investimentos, cartão). Base para os gráficos de evolução.

RF-INV-005 — Evolução patrimonial
Gráfico de evolução do patrimônio e de cada classe ao longo do tempo (30/90/365 dias).

### 6.10 Recorrentes / Assinaturas
RF-REC-001 — Detecção
Job periódico analisa o histórico e identifica cobranças recorrentes: mesmo merchant/descrição normalizada, valor similar (±10%), cadência regular (mensal/semanal/anual). Agrupa em `RecurringTransaction`.
Aceite: Netflix, Spotify, academia aparecem como recorrentes com valor médio e próxima cobrança estimada.

RF-REC-002 — Painel de assinaturas
Lista de recorrentes com: nome, categoria, valor médio, frequência, última cobrança, próxima estimada, total gasto no ano. Marcar como "assinatura" e "ativa/cancelada".

RF-REC-003 — Custo recorrente mensal
Somatório do custo recorrente mensal estimado (quanto de gasto já está "comprometido" por mês).

RF-REC-004 — Alerta de nova recorrente / aumento
Notifica quando detecta nova recorrência ou quando o valor de uma recorrente sobe além de um limiar.

### 6.11 Metas de economia
RF-GOAL-001 — Criar meta
Meta com: nome, valor-alvo, data-alvo (opcional), conta/investimento vinculado (opcional), ícone, cor.

RF-GOAL-002 — Progresso
Progresso da meta calculado pelo saldo da conta/investimento vinculado, ou por aportes marcados. Mostra % concluído, valor restante, ritmo necessário para bater a data-alvo.

RF-GOAL-003 — Estados
ACTIVE, COMPLETED (atingiu o alvo), ARCHIVED. Notifica ao concluir.

### 6.12 Relatórios & Exportação
RF-REP-001 — Relatórios
- Gastos por categoria (período)
- Receita vs. despesa (mensal/anual)
- Fluxo por conta
- Evolução de patrimônio
- Faturas de cartão por mês
- Recorrentes/assinaturas
- Orçamento realizado vs. planejado
Todos com filtro por período, conta, categoria.

RF-REP-002 — Exportação
Exportar transações e relatórios em **CSV** e **Excel (.xlsx)**. Exportar snapshot de patrimônio.
Aceite: exportar transações de um período gera arquivo abrível no Excel com colunas corretas.

RF-REP-003 — Consultas otimizadas
Relatórios agregados usam Dapper com SQL direto para performance.

### 6.13 Alertas & Notificações
RF-NOT-001 — Canais
Notificações via **Telegram Bot** (principal) e **e-mail SMTP** (opcional). Configurar token/chat id do Telegram e SMTP em Configurações.

RF-NOT-002 — Tipos de alerta
- Orçamento em 90%/100% (por categoria e global)
- Saldo baixo em conta (< limiar configurável)
- Fatura de cartão fechando/vencendo (X dias antes)
- Transação grande (> limiar)
- Nova assinatura detectada / aumento de recorrente
- Erro de sincronização / conexão em LOGIN_ERROR
- Meta concluída

RF-NOT-003 — Central de notificações
Sino no topbar com histórico (`Notification`): título, mensagem, lida/não lida, data, canal.

RF-NOT-004 — Regras de alerta
CRUD de `AlertRule`: tipo, limiar, escopo (categoria/conta), canal, ativo.

### 6.14 Configurações & Backup
RF-CFG-001 — Conexões
Gerenciar conexões Pluggy (adicionar, reconectar, remover, sync).

RF-CFG-002 — Categorias e regras
Gerenciar categorias e regras de categorização.

RF-CFG-003 — Alertas
Configurar canais (Telegram/SMTP) e regras de alerta.

RF-CFG-004 — Preferências
Fuso horário, moeda base (BRL default), horários de sync, tema padrão.

RF-CFG-005 — Backup/restore
Exportar dump lógico dos dados (JSON/CSV) e instrução de backup do Postgres. (Dados na Pluggy sempre re-sincronizáveis.)

## 7. Requisitos não funcionais

### 7.1 Performance
- Dashboard carrega em < 2s com 24 meses de histórico.
- Extrato pagina 50 transações por request; busca responde < 500ms.
- Relatórios agregados (Dapper) < 1s para 2 anos de dados.

### 7.2 Segurança
- Senha com hash Argon2id (ou BCrypt cost ≥ 12).
- 2FA TOTP obrigatório após ativação; secrets e códigos de recuperação criptografados em repouso.
- Credenciais Pluggy (`CLIENT_ID/SECRET`) e tokens (Telegram/SMTP) **só em variáveis de ambiente / secrets**, nunca no banco em texto puro.
- **O SimpleFinance nunca armazena credenciais bancárias** — elas ficam na Pluggy via Connect Widget.
- Webhook valida assinatura/segredo; endpoint idempotente.
- JWT com expiração curta + refresh rotativo.
- HTTPS obrigatório (reverse proxy/Traefik/Caddy com TLS).
- Rate limit no login e no webhook.

### 7.3 Privacidade
- Dados financeiros ficam no Postgres do próprio usuário. Nenhum envio a terceiros além da própria Pluggy (que é o agregador de origem).
- Sem telemetria/analytics externos.

### 7.4 Responsividade
- Web responsiva. Prioridade: desktop → mobile. Todas as funções acessíveis no celular via navegador.

### 7.5 Disponibilidade & operação
- Deploy Docker Compose (api + front + postgres + reverse proxy).
- Migrations automáticas no start.
- Logs estruturados (Serilog) em arquivo/console.
- Backup do Postgres documentado (volume + `pg_dump` agendado opcional).

### 7.6 Observabilidade
- Healthcheck (`/health`) para api e conexão com Postgres/Pluggy.
- Log de cada sync e webhook.

### 7.7 Restrição de custo (Pluggy)
- MVP roda em **sandbox** e no **tier gratuito para uso pessoal** da Pluggy. A produção com dados reais depende do plano/limite pessoal vigente. A arquitetura isola o provedor (§5.3) caso seja necessário trocar/renegociar. Ver risco em §19.

## 8. UX & interface

### 8.1 Layout principal
- Sidebar recolhível (navegação) + topbar (busca global, sino de notificações, seletor de período, avatar/menu).
- Área de conteúdo principal.
- Tema claro/escuro com toggle.

### 8.2 Tema
Pasta `src/theme` com tokens: `colors.ts`, `typography.ts`, `spacing.ts`, `radius.ts`, `shadows.ts`, `light.ts`, `dark.ts`, `index.ts`. Estados: sucesso, alerta, erro, info; cores por classe financeira (receita=verde, despesa=vermelho, investimento=azul, transferência=cinza).

### 8.3 Dashboard inicial (o que mostra)
Patrimônio líquido, saldo em contas, dívida de cartões, receita/despesa/saldo do mês, top categorias de gasto, orçamentos em risco, evolução de patrimônio, próximos vencimentos.

### 8.4 Experiência priorizada
Como é single-user, a experiência prioriza, nesta ordem:
1. Bater o olho no dashboard e entender a situação.
2. Ver/filtrar o extrato e recategorizar rápido.
3. Ajustar orçamento e ver estouros.
4. Acompanhar patrimônio/investimentos.

## 9. Fluxo de navegação

### 9.1 Árvore de menu
```
Dashboard
Transações
Orçamento
Investimentos
Patrimônio
Recorrentes
Metas
Relatórios
Configurações
  Conexões (Pluggy)
  Categorias
  Regras
  Alertas & Notificações
  Preferências
  Backup
```

### 9.2 Fluxo — Conectar banco (primeiro uso)
Setup usuário → Login (+2FA) → Configurações → Conexões → "Conectar instituição" → abre Pluggy Connect Widget → escolhe banco → autentica na Pluggy → retorna itemId → primeiro sync → dados aparecem no Dashboard.

### 9.3 Fluxo — Dia a dia (dados chegam sozinhos)
Webhook `item/updated` / job diário → sync reconcilia → aplica regras de categoria → dispara alertas se necessário → Dashboard/extrato já atualizados na próxima visita.

### 9.4 Fluxo — Entender gastos e ajustar
Dashboard → clica numa categoria → Extrato filtrado → recategoriza uma transação → "aplicar a parecidas" → cria regra → orçamento e dashboard refletem.

### 9.5 Fluxo — Orçamento
Orçamento → define/ajusta tetos por categoria → acompanha gasto vs. planejado → recebe alerta ao chegar em 90%/100%.

### 9.6 Fluxo — Recorrentes
Job detecta recorrências → Recorrentes lista assinaturas → usuário marca/cancela → vê custo mensal comprometido.

## 10. Telas do MVP

### 10.1 Autenticação
- Setup inicial (criar usuário único)
- Login (e-mail + senha)
- Verificação 2FA (código TOTP / código de recuperação)
- Ativar 2FA (QR code + códigos de recuperação)
- Trocar senha

### 10.2 Dashboard
- Cards de resumo (patrimônio, saldo, dívida cartão, receita/despesa/saldo do mês)
- Gráfico gastos por categoria
- Gráfico evolução de patrimônio
- Orçamentos em risco
- Próximos vencimentos / recorrentes

### 10.3 Transações
- Extrato unificado com scroll infinito
- Filtros (período, conta, categoria, tipo, valor, status)
- Busca por texto
- Recategorizar (inline + em massa)
- Detalhe da transação (raw, merchant, notas, ocultar)

### 10.4 Orçamento
- Lista de categorias orçadas com barra de progresso
- Definir/editar teto
- Estado OK/Atenção/Estourado
- Teto global opcional

### 10.5 Investimentos
- Total investido + distribuição por tipo/emissor (gráfico)
- Lista de posições com valor, aplicado, rendimento, vencimento

### 10.6 Patrimônio
- Patrimônio líquido atual + composição (contas, investimentos, dívida)
- Gráfico de evolução (30/90/365 dias)

### 10.7 Recorrentes
- Lista de assinaturas/recorrentes (valor médio, frequência, próxima cobrança)
- Custo mensal comprometido
- Marcar assinatura / ativa-cancelada

### 10.8 Metas
- Cards de metas com progresso
- Criar/editar meta (alvo, data, conta vinculada)

### 10.9 Relatórios
- Seleção de relatório + filtros
- Visualização (tabela/gráfico)
- Exportar CSV/Excel

### 10.10 Configurações
- Conexões (lista de bancos, status, reconectar, remover, sync)
- Categorias (árvore, CRUD)
- Regras (CRUD, prioridade, testar)
- Alertas & Notificações (canais Telegram/SMTP, regras de alerta, central)
- Preferências (tema, fuso, moeda, horários de sync)
- Backup (exportar dados)

## 11. Modelo de dados

> Todas as tabelas operacionais carregam `UserId` (sempre 1 usuário no MVP, mas preparado para multiusuário). Soft delete (`DeletedAt`) nas entidades relevantes. Auditoria mínima via `CreatedAt/UpdatedAt`.

### 11.1 Usuário & Auth
```
User:
- Id, Name, Email, PasswordHash
- TotpSecret (encrypted), TotpEnabled
- RecoveryCodesHash (json/list)
- LastLoginAt, CreatedAt, UpdatedAt

RefreshToken:
- Id, UserId, TokenHash, ExpiresAt, RevokedAt, CreatedAt
```

### 11.2 Integração Pluggy
```
Connection:               # wrapper do "Item" da Pluggy
- Id, UserId
- PluggyItemId, ConnectorId, ConnectorName, InstitutionName, ImageUrl
- Status (UPDATING|UPDATED|LOGIN_ERROR|WAITING_USER_INPUT|OUTDATED|ERROR)
- StatusDetail, ExecutionStatus, ErrorCode, ErrorMessage
- LastSyncAt, CreatedAt, UpdatedAt, DeletedAt

WebhookEvent:
- Id, EventType, PluggyItemId, PayloadJson
- ReceivedAt, ProcessedAt, Status (PENDING|PROCESSED|FAILED), Error

SyncLog:
- Id, ConnectionId, TriggeredBy (WEBHOOK|CRON|MANUAL)
- StartedAt, FinishedAt, Status
- TransactionsAdded, TransactionsUpdated, Error
```

### 11.3 Contas & Cartões
```
Account:
- Id, UserId, ConnectionId
- PluggyAccountId, Type (BANK|CREDIT), Subtype (CHECKING_ACCOUNT|SAVINGS_ACCOUNT|CREDIT_CARD)
- Name, MarketingName, NumberMasked, Owner
- Balance, CurrencyCode
- IsHidden, CreatedAt, UpdatedAt, DeletedAt

CreditCardDetail:              # 1:1 com Account quando Type=CREDIT
- Id, AccountId
- Brand, Level, Limit, AvailableCredit, BalanceCloseDate, BalanceDueDate
- MinimumPayment, LastStatementBalance
- CreatedAt, UpdatedAt
```

### 11.4 Transações
```
Transaction:
- Id, UserId, AccountId
- PluggyTransactionId
- Date, Description, DescriptionRaw
- Amount, Type (DEBIT|CREDIT), CurrencyCode, BalanceAfter
- Status (POSTED|PENDING)
- MerchantName, MerchantDocument
- PluggyCategory, PluggyCategoryId
- CategoryId (FK -> Category, override do usuário)
- IsTransfer, IsHidden, Notes
- RecurringTransactionId (FK, nullable)
- CreatedAt, UpdatedAt

# Preparado p/ depois (não implementado no MVP):
TransactionSplit:
- Id, TransactionId, CategoryId, Amount, Notes
```

### 11.5 Categorias & Regras
```
Category:
- Id, UserId
- Name, ParentCategoryId (nullable)
- Type (EXPENSE|INCOME|TRANSFER)
- Icon, Color, IsSystem, IsHidden
- CreatedAt, UpdatedAt

CategoryMapping:               # Pluggy category -> Category default
- Id, PluggyCategoryId, PluggyCategoryName, CategoryId

CategoryRule:
- Id, UserId
- Priority, MatchField (DESCRIPTION|MERCHANT|PLUGGY_CATEGORY|AMOUNT)
- MatchOperator (CONTAINS|EQUALS|REGEX|RANGE)
- MatchValue, AmountMin, AmountMax
- CategoryId, IsActive
- CreatedAt, UpdatedAt
```

### 11.6 Orçamento
```
Budget:
- Id, UserId
- CategoryId (nullable = orçamento global)
- Year, Month
- LimitAmount
- CreatedAt, UpdatedAt
```

### 11.7 Investimentos & Patrimônio
```
Investment:
- Id, UserId, ConnectionId
- PluggyInvestmentId
- Name, Type (MUTUAL_FUND|FIXED_INCOME|EQUITY|ETF|SECURITY|COE|CRYPTO), Subtype
- Institution, IssuerName
- Balance, Value, Quantity, AmountInvested
- LastMonthRate, AnnualRate
- Date, DueDate, CurrencyCode
- CreatedAt, UpdatedAt, DeletedAt

NetWorthSnapshot:
- Id, UserId, Date
- TotalAssets, TotalLiabilities, NetWorth
- AccountsBalance, InvestmentsBalance, CreditDebt
- CreatedAt
```

### 11.8 Recorrentes & Metas
```
RecurringTransaction:
- Id, UserId
- NormalizedMerchant, DisplayName
- CategoryId
- AverageAmount, Frequency (WEEKLY|MONTHLY|YEARLY)
- FirstSeenDate, LastSeenDate, NextExpectedDate
- OccurrenceCount, IsSubscription, IsActive
- CreatedAt, UpdatedAt

Goal:
- Id, UserId
- Name, TargetAmount, CurrentAmount, TargetDate
- LinkedAccountId (nullable), LinkedInvestmentId (nullable)
- Icon, Color, Status (ACTIVE|COMPLETED|ARCHIVED)
- CreatedAt, UpdatedAt
```

### 11.9 Alertas & Notificações
```
AlertRule:
- Id, UserId
- Type (BUDGET_THRESHOLD|LOW_BALANCE|INVOICE_DUE|LARGE_TRANSACTION|NEW_RECURRING|SYNC_ERROR|GOAL_REACHED)
- Threshold, CategoryId (nullable), AccountId (nullable), DaysBefore
- Channel (TELEGRAM|EMAIL), IsActive
- CreatedAt, UpdatedAt

Notification:
- Id, UserId, Type, Title, Message
- Channel, IsRead, SentAt, CreatedAt
```

## 12. Backend

### 12.1 Stack
- .NET 10 / ASP.NET Core
- PostgreSQL
- EF Core (domínio/CRUD) + Dapper (relatórios)
- FluentValidation
- Hangfire (jobs) + PostgreSQL storage
- Serilog
- Refit/HttpClientFactory para o cliente Pluggy
- Docker Compose

### 12.2 Arquitetura
Monólito modular por domínio, single-user. Estrutura:
```
src/
  SimpleFinance.Api/            # controllers, webhook, auth, DI
  SimpleFinance.Application/    # use cases, services, DTOs, validators
  SimpleFinance.Domain/         # entidades, enums, regras
  SimpleFinance.Infrastructure/ # EF, Dapper, Pluggy client, Hangfire jobs, notif
  SimpleFinance.Shared/
tests/
  SimpleFinance.UnitTests/
```
**Abstração de provedor:** `IFinancialDataProvider` na Application, implementado por `PluggyDataProvider` na Infrastructure (prepara troca futura — §5.3).

### 12.3 Módulos de domínio
```
Auth
Connections (Pluggy)
Sync
Accounts
Transactions
Categories & Rules
Budgets
Investments & NetWorth
Recurring
Goals
Reports
Notifications
Settings
```

### 12.4 API (REST) — endpoints de exemplo
```
# Auth
POST   /api/auth/setup                 # cria usuário único (1x)
POST   /api/auth/login
POST   /api/auth/2fa/verify
POST   /api/auth/2fa/enable
POST   /api/auth/refresh
POST   /api/auth/change-password

# Pluggy / Conexões
POST   /api/pluggy/connect-token       # gera connect token p/ o widget
GET    /api/connections
POST   /api/connections                # registra item após widget (itemId)
POST   /api/connections/{id}/sync
POST   /api/connections/{id}/reconnect-token
DELETE /api/connections/{id}
POST   /api/webhooks/pluggy            # receiver (público, validado)

# Contas
GET    /api/accounts
PATCH  /api/accounts/{id}              # ocultar/mostrar

# Transações
GET    /api/transactions               # filtros: from,to,accountId,categoryId,type,q,status,min,max
GET    /api/transactions/{id}
PATCH  /api/transactions/{id}          # categoria, notas, ocultar, isTransfer
POST   /api/transactions/recategorize  # em massa por regra/critério

# Categorias & Regras
GET    /api/categories
POST   /api/categories
PUT    /api/categories/{id}
GET    /api/rules
POST   /api/rules
PUT    /api/rules/{id}
POST   /api/rules/{id}/apply           # aplica retroativo

# Orçamento
GET    /api/budgets?year=&month=
PUT    /api/budgets                    # upsert por categoria/mês

# Investimentos & Patrimônio
GET    /api/investments
GET    /api/networth                   # atual
GET    /api/networth/history?range=

# Recorrentes / Metas
GET    /api/recurring
PATCH  /api/recurring/{id}
GET    /api/goals
POST   /api/goals
PUT    /api/goals/{id}

# Relatórios
GET    /api/reports/spending-by-category
GET    /api/reports/income-expense
GET    /api/reports/export             # csv|xlsx

# Notificações
GET    /api/notifications
POST   /api/notifications/{id}/read
GET    /api/alert-rules
POST   /api/alert-rules
```

### 12.5 Validação
FluentValidation em todos os commands/DTOs de entrada (regras, orçamentos, metas, alertas).

### 12.6 Jobs (Hangfire)
- `DailySyncJob` — sync de todas as conexões (2x/dia configurável)
- `NetWorthSnapshotJob` — snapshot diário
- `RecurringDetectionJob` — detecção de recorrentes (diário/semanal)
- `AlertEvaluationJob` — avalia regras de alerta e dispara notificações
- `PluggyApiKeyRefresh` — renova API key quando necessário
- `WebhookProcessorJob` — processa eventos enfileirados

### 12.7 Cliente Pluggy (Infrastructure)
`PluggyClient`: `/auth` (API key), `/connect_token`, `/items`, `/accounts`, `/transactions`, `/investments`, `DELETE /items/{id}`. Trata paginação, retry/backoff, renovação de API key, mapeamento para entidades de domínio.

### 12.8 Logs
Serilog estruturado (console/arquivo). Correlação por sync/webhook.

## 13. Frontend

### 13.1 Stack
React + Vite + TypeScript, React Router, TanStack Query (data fetching/cache), Recharts (gráficos), Zod (validação de forms), tema próprio.

### 13.2 Estrutura sugerida
```
src/
  app/
  components/         # UI base
  features/
    auth/
    dashboard/
    transactions/
    budget/
    investments/
    networth/
    recurring/
    goals/
    reports/
    settings/
      connections/
      categories/
      rules/
      alerts/
  hooks/
  services/           # api client, pluggy connect
  theme/
  utils/
```

### 13.3 Tema
`theme/{colors,typography,spacing,radius,shadows,light,dark,index}.ts` — claro/escuro, cores financeiras semânticas.

### 13.4 Componentes base
Button, Input, Select, Modal, Drawer, Table/DataGrid, Card, Badge, Tabs, DateRangePicker, ProgressBar, Chart (wrappers Recharts), Sidebar, Topbar, PageHeader, EmptyState, ConfirmDialog, Toast, NotificationBell, MoneyText (formata BRL, cor por sinal).

### 13.5 Pluggy Connect
Integrar o **Pluggy Connect Widget** (SDK JS `pluggy-connect`) no fluxo de Conexões: backend gera connect token → front abre o widget → callback `onSuccess(itemId)` → POST `/api/connections`.

## 14. Regras de negócio importantes
1. **Fonte de verdade = Pluggy.** Não há criação manual de transação no MVP; o sistema só espelha e enriquece (categoria/notas/flags).
2. **Idempotência de sync.** Chaves Pluggy (`PluggyTransactionId` etc.) evitam duplicidade; sync é sempre reconciliação.
3. **Credenciais bancárias nunca tocam o SimpleFinance** — só a Pluggy as detém.
4. **Segredos só em env/secrets** (Pluggy client/secret, Telegram, SMTP), nunca versionados nem em texto puro no banco.
5. **Transferência interna não é gasto** — transações marcadas `IsTransfer` saem de receita/despesa e orçamento.
6. **Regras de categoria têm prioridade** sobre o mapeamento default Pluggy; regra do usuário sempre vence.
7. **Soft delete** em contas/transações/investimentos/conexões ao remover uma conexão (dados re-sincronizáveis, mas preserva histórico e categorização até confirmação).
8. **Orçamento é por mês/categoria**; ausência de orçamento não bloqueia nada, só não acompanha.
9. **Patrimônio = ativos − passivos**, com snapshot diário imutável para evolução.
10. **UserId em toda query** (mesmo com 1 usuário) — prepara multiusuário sem refatorar.
11. **Webhook idempotente e validado** — reprocessar o mesmo evento não corrompe dados.
12. **2FA obrigatório** para dados financeiros uma vez ativado; recuperação por códigos de uso único.

## 15. Roadmap de implementação

### Fase 1 — Base
- Projeto .NET 10 + React/Vite + Postgres + Docker Compose
- Auth single-user (setup, login, JWT, 2FA TOTP)
- Layout base + tema claro/escuro + componentes base

### Fase 2 — Pluggy & Sync
- `IFinancialDataProvider` + `PluggyClient` (auth, connect token, items, accounts, transactions, investments)
- Fluxo Connect Widget (front) + registrar conexão
- Webhook receiver + processamento
- `DailySyncJob` + sync manual + reconciliação idempotente + SyncLog

### Fase 3 — Contas, Cartões & Transações
- Importar contas/cartões (limite, fatura, vencimento)
- Extrato unificado, filtros, busca, detalhe
- Recategorizar, notas, ocultar, detectar transferências

### Fase 4 — Categorias & Regras
- Árvore de categorias + seed + mapeamento Pluggy
- Motor de regras + aplicar no sync + recategorização em massa

### Fase 5 — Dashboard & Orçamento
- Dashboard consolidado + gráficos
- Orçamento por categoria + estados + acompanhamento

### Fase 6 — Investimentos & Patrimônio
- Importar investimentos + tela de consolidação
- Cálculo de patrimônio + `NetWorthSnapshotJob` + evolução

### Fase 7 — Recorrentes, Metas, Relatórios
- `RecurringDetectionJob` + painel de assinaturas
- Metas + progresso
- Relatórios + exportação CSV/Excel (Dapper)

### Fase 8 — Alertas & Polimento
- Canais Telegram/SMTP + `AlertEvaluationJob` + regras de alerta + central
- Backup/export, responsividade, testes dos domínios críticos (sync, regras, orçamento, patrimônio)

## 16. Prompts para vibecode

### Prompt 1 — Base do projeto
Criar app **SimpleFinance**: backend .NET 10 (ASP.NET Core) + frontend React+Vite+TypeScript + PostgreSQL. Arquitetura monólito modular por domínio com projetos Api/Application/Domain/Infrastructure/Shared e tests/UnitTests. Configurar EF Core (Postgres), FluentValidation, Serilog, Hangfire (storage Postgres) e Docker Compose (api, front, postgres). É **single-user**: todas as entidades carregam `UserId`, mas sem multi-tenant. Criar migrations automáticas no start e healthcheck `/health`.

### Prompt 2 — Auth single-user + 2FA
Implementar autenticação de usuário único: endpoint de setup que cria o único `User` (nome/e-mail/senha, hash Argon2id) e se bloqueia após o primeiro uso; login com JWT (access+refresh rotativo); 2FA TOTP (ativação com QR + 8 códigos de recuperação de uso único, secret criptografado); troca de senha. Telas React: setup, login, verificação 2FA, ativar 2FA, trocar senha. Guardas de rota no front.

### Prompt 3 — Tema e layout
Criar layout com sidebar recolhível, topbar (busca global, sino de notificações, seletor de período, menu do usuário) e área de conteúdo. Tema próprio em `src/theme` (claro/escuro, tokens de cor/tipografia/espaçamento, cores financeiras semânticas). Componentes base: Button, Input, Select, Modal, Drawer, Table/DataGrid, Card, Badge, Tabs, DateRangePicker, ProgressBar, Chart (Recharts), Toast, EmptyState, ConfirmDialog, NotificationBell, MoneyText (formata BRL com cor por sinal).

### Prompt 4 — Cliente Pluggy + conexões
Implementar `IFinancialDataProvider` (Application) e `PluggyDataProvider`/`PluggyClient` (Infrastructure) cobrindo `/auth` (API key com cache/renovação), `/connect_token`, `/items`, `/accounts`, `/transactions`, `/investments`, `DELETE /items/{id}`, com paginação e retry/backoff. Flag `PLUGGY_ENV=sandbox|production` e credenciais via env. Endpoints: gerar connect token, registrar `Connection` a partir do `itemId`, listar/remover conexão, token de reconexão. Front: tela de Conexões + integração do **Pluggy Connect Widget** (abre com connect token, `onSuccess(itemId)` → POST `/api/connections`). Mapear estados do Item (UPDATING/UPDATED/LOGIN_ERROR/WAITING_USER_INPUT/OUTDATED/ERROR) com ação sugerida.

### Prompt 5 — Sync (webhook + job + reconciliação)
Implementar sincronização: endpoint público `POST /api/webhooks/pluggy` que valida assinatura, persiste `WebhookEvent` e enfileira processamento (eventos item/transactions/connector). `DailySyncJob` (Hangfire, 2x/dia configurável) e sync manual por conexão/global. Reconciliação **idempotente** por `PluggyTransactionId/AccountId/InvestmentId` (insere/atualiza/marca deletado, nunca duplica). Registrar `SyncLog` (gatilho, contagem, status, erro). Ao inserir transações novas, aplicar o motor de regras de categoria.

### Prompt 6 — Contas, cartões e extrato
Importar e exibir `Account` (BANK/CREDIT) e `CreditCardDetail` (limite, disponível, fatura, fechamento, vencimento, bandeira). Tela de contas/cartões com saldos, limite usado e vencimento; opção ocultar conta. Implementar `Transaction` completa e a tela de **Extrato unificado** com scroll infinito, filtros (período, conta, categoria, tipo, status, faixa de valor, texto), detalhe (raw, merchant, notas), ações de recategorizar/ocultar/marcar transferência. Heurística de detecção de transferências internas.

### Prompt 7 — Categorias e motor de regras
Criar `Category` (árvore, tipo EXPENSE/INCOME/TRANSFER, ícone, cor, seed padrão + `CategoryMapping` Pluggy→sistema). Implementar `CategoryRule` (MatchField/Operator/Value/prioridade → categoria) executado no sync e sob demanda, com **recategorização retroativa em massa** e sugestão de "aplicar a parecidas" ao recategorizar uma transação. Telas de Categorias e Regras (CRUD, prioridade, testar regra).

### Prompt 8 — Dashboard e orçamento
Implementar Dashboard consolidado (patrimônio líquido, saldo em contas, dívida de cartões, receita/despesa/saldo do mês, gastos por categoria, evolução de patrimônio, orçamentos em risco, próximos vencimentos, seletor de período). Implementar `Budget` (teto por categoria/mês + global opcional), tela de Orçamento com barra de progresso, estados OK/Atenção/Estourado, projeção de fim de mês. Consultas agregadas com Dapper.

### Prompt 9 — Investimentos e patrimônio
Importar `Investment` (tipo/subtipo, instituição, valor, aplicado, taxa, vencimento). Tela de Investimentos (total, distribuição por tipo/emissor, lista). Cálculo de patrimônio líquido (ativos − passivos), `NetWorthSnapshotJob` diário e tela de Patrimônio com evolução (30/90/365 dias).

### Prompt 10 — Recorrentes, metas, relatórios, exportação
Implementar `RecurringDetectionJob` (agrupa por merchant normalizado + valor similar + cadência) e painel de Recorrentes (valor médio, frequência, próxima cobrança, custo mensal comprometido, marcar assinatura/cancelada). Implementar `Goal` (alvo, data, conta/investimento vinculado, progresso, estados). Relatórios (gastos por categoria, receita×despesa, patrimônio, faturas, recorrentes, orçamento realizado) com **exportação CSV e Excel** via Dapper.

### Prompt 11 — Alertas e notificações
Implementar canais **Telegram Bot** (principal) e **e-mail SMTP** (opcional), `AlertRule` (BUDGET_THRESHOLD, LOW_BALANCE, INVOICE_DUE, LARGE_TRANSACTION, NEW_RECURRING, SYNC_ERROR, GOAL_REACHED) e `AlertEvaluationJob` que avalia e dispara. Central de notificações (sino + histórico `Notification`, lida/não lida). Configuração de canais e regras em Configurações.

### Prompt 12 — Polimento, backup e testes
Backup/export de dados (JSON/CSV) + doc de backup do Postgres. Revisar responsividade (desktop→mobile), rate limit em login/webhook, HTTPS via reverse proxy no compose. Testes unitários dos domínios críticos: reconciliação de sync (idempotência), motor de regras, cálculo de orçamento, cálculo de patrimônio, detecção de recorrentes. Garantir `UserId` em todas as queries e segredos só em env.

## 17. Critérios de sucesso do MVP
O MVP está pronto quando, sozinho, o usuário consegue:
- Criar seu usuário único e ativar 2FA
- Conectar uma instituição via Pluggy Connect (sandbox)
- Ver contas, cartões e investimentos importados automaticamente
- Receber transações novas via webhook + job sem apertar botão
- Ver o extrato unificado, filtrar, buscar e recategorizar
- Criar regras que categorizam novas transações sozinhas
- Ver o dashboard com patrimônio, gastos do mês e evolução
- Definir orçamento por categoria e ser alertado ao estourar
- Ver investimentos consolidados e evolução do patrimônio
- Ver assinaturas recorrentes e o custo mensal comprometido
- Criar metas de economia e acompanhar progresso
- Exportar transações/relatórios em CSV/Excel
- Receber alertas no Telegram/e-mail
- Usar tudo em desktop e celular (web responsiva)

## 18. Prioridades de desenvolvimento
### Alta
- Base + auth/2FA
- Cliente Pluggy + conexões + sync (webhook/job/idempotência)
- Contas/cartões + extrato + categorias/regras
- Dashboard + orçamento

### Média
- Investimentos + patrimônio + snapshots
- Recorrentes + metas
- Relatórios + exportação

### Baixa (ainda no MVP)
- Alertas/notificações (Telegram/SMTP)
- Backup/export, polimento, testes

### Fora do MVP
- Multiusuário, entrada manual, pagamentos, IA de categorização/projeção, split de transação, multi-moeda real, app nativo

## 19. Contexto de mercado & fontes

### Tendências
- Open Finance Brasil já passou de **42 milhões** de usuários (2024), tornando agregação automática o padrão esperado em apps de finanças. ([napratica.org.br](https://napratica.org.br/noticias/6-apps-gratuitos-para-organizar-a-vida-financeira))
- Apps líderes escondem a **sincronização bancária (Open Finance) atrás de plano pago**, deixando espaço para uma solução auto-hospedada de dono único. ([mobills.com.br](https://www.mobills.com.br/blog/aplicativos/apps-de-controle-financeiro/))
- Categorias emergentes (ex.: FinVibe) combinam **Open Finance + patrimônio + comportamento**, sinal de que patrimônio consolidado é diferencial valorizado. ([finvibe.app](https://www.finvibe.app/blog/mobills-x-organizze-x-finvibe-x-minhas-economias-qual-o-melhor-app-de-financas))

### Concorrentes
| Nome | Posicionamento | Preço | Forte | Fraco |
|---|---|---|---|---|
| Mobills | Controle financeiro completo, popular | Grátis (manual) / Premium pago p/ Open Finance | Base grande, relatórios | Sync bancário só no pago; dados na nuvem deles |
| Organizze | Interface elegante, suporte humano | ~R$199,90/ano (sem plano grátis fixo, 7 dias trial) | UX, consistência | Sem versão grátis; SaaS fechado |
| FinVibe | Open Finance + patrimônio + comportamento | Freemium | Patrimônio completo, brasileiro | SaaS, dados fora do controle do usuário |
| Minhas Economias | Controle simples | Freemium | Simplicidade | Recursos limitados |

### Oportunidade / gap explorado
Nenhum líder é **auto-hospedado e de dono único**. O SimpleFinance ocupa exatamente esse nicho pessoal: mesmos benefícios de Open Finance (via Pluggy) sem mensalidade de app e com os dados no servidor do próprio usuário.

### Risco a monitorar — custo Pluggy
Relatos públicos indicam planos de produção da Pluggy a partir de **~R$2.500/mês** para uso comercial, com sandbox e trial gratuitos. Para **uso estritamente pessoal** há tier gratuito (caso do usuário). Mitigação no design: `IFinancialDataProvider` isola o provedor para eventual troca (Belvo, Tecnospeed) sem reescrever o domínio. ([tabnews.com.br](https://www.tabnews.com.br/GuilhermeVieira/estou-desenvolvendo-um-app-de-financas-pessoais-e-nao-consigo-pagar-o-open-finance-pluggy-r2-5k-mes-belvo-r6k-mes-tecnospeed-r1-5k-de-entrada-r540))

### Fontes
- Pluggy — site e docs: https://www.pluggy.ai/ · https://docs.pluggy.ai/ · https://www.pluggy.ai/pricing
- Pluggy quickstart / connect: https://github.com/pluggyai/quickstart · https://github.com/pluggyai/meu-pluggy
- Comparativos de apps BR: https://www.mobills.com.br/blog/aplicativos/apps-de-controle-financeiro/ · https://www.finvibe.app/blog/mobills-x-organizze-x-finvibe-x-minhas-economias-qual-o-melhor-app-de-financas · https://napratica.org.br/noticias/6-apps-gratuitos-para-organizar-a-vida-financeira
- Custo Open Finance (relato): https://www.tabnews.com.br/GuilhermeVieira/estou-desenvolvendo-um-app-de-financas-pessoais-e-nao-consigo-pagar-o-open-finance-pluggy-r2-5k-mes-belvo-r6k-mes-tecnospeed-r1-5k-de-entrada-r540

## 20. Perguntas em aberto
- **Alertas:** canal principal confirmado Telegram; definir se e-mail SMTP entra já no MVP ou fica em segundo momento.
- **Detecção de recorrentes:** afinar limiares (janela de cadência, tolerância de valor) com dados reais.
- **Retenção de histórico** ao remover conexão: soft delete assumido; confirmar se deve haver expurgo definitivo opcional.
- **Multi-moeda:** assume-se BRL; guardar `CurrencyCode` mas não converter no MVP.

## Conclusão
O SimpleFinance nasce como um app pessoal e auto-hospedado que transforma o Open Finance (via Pluggy) numa central financeira privada de dono único. O fluxo essencial ponta a ponta é:

**Conectar banco (Pluggy Connect) → sync automático (webhook + job) → transações categorizadas por regras → Dashboard/Orçamento/Patrimônio → Recorrentes/Metas → Relatórios/Alertas.**

O MVP entrega automação real (nada de digitar), consolidação total (contas, cartões, investimentos) e controle (orçamento, recorrentes, metas) — com a arquitetura já isolando o provedor de dados para não ficar refém de um único agregador.
