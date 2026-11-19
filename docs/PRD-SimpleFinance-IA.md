# PRD — SimpleFinance IA (Camada de Inteligência)
_v0.1 · 2026-07-10 · rascunho gerado com prd-wizard_

> Extensão do sistema existente. **Fonte primária do sistema base:** [PRD-SimpleFinance.md](PRD-SimpleFinance.md) e [CLAUDE.md](CLAUDE.md). Este PRD **não** reescreve o app — adiciona uma camada de IA sobre o domínio já pronto (contas, transações, categorias, orçamento, patrimônio, recorrentes, metas, alertas). O que já está deferido no PRD base (§5.2 "Categorização por IA/LLM", "Previsão/projeção de fluxo de caixa com IA") e o que §5.3 já preparou ("o motor de regras é plugável", `IFinancialDataProvider`) são exatamente os ganchos que esta camada preenche.

---

## 1. Visão geral

### 1.1 Nome
**SimpleFinance IA** — camada de inteligência do SimpleFinance.

### 1.2 Descrição
Camada de IA sobre o SimpleFinance que (1) categoriza automaticamente as transações que o motor de regras determinístico não pega — atacando o problema real de ~93% "Sem categoria"; (2) responde perguntas em linguagem natural sobre os próprios dados financeiros; (3) gera insights, detecta anomalias, projeta fluxo/saldo e escreve um resumo mensal. Continua **single-user, auto-hospedado, sem lançamento manual** — a IA só interpreta os dados que a Pluggy já traz. Barato por design: DeepSeek como LLM padrão, embeddings OpenAI, teto de gasto mensal.

### 1.3 Stack (em bullets)
- **LLM:** DeepSeek via API OpenAI-compatível (`https://api.deepseek.com`). `deepseek-chat` (V3) como padrão; `deepseek-reasoner` (R1) só onde o raciocínio justifica (resumo mensal, planejamento de ações). Chave em env.
- **Embeddings:** OpenAI `text-embedding-3-small` (1536 dims). Chave em env.
- **Vetores:** extensão **pgvector** no PostgreSQL existente (mesma instância, sem infra nova).
- **Acesso a dados pela IA:** **tool-calling** (números exatos via consultas parametrizadas/serviços existentes) **+ RAG** (similaridade semântica p/ categorização e busca).
- **Backend:** .NET 10 / ASP.NET Core — novo módulo `SimpleFinance.Ai` atrás de abstrações `ILlmClient` / `IEmbeddingClient` (espelha o padrão `IFinancialDataProvider`).
- **Jobs:** Hangfire (já no projeto) — embeddings, categorização em lote, anomalias, previsão, resumo mensal. **Batch noturno** por padrão.
- **Frontend:** React + Vite + TS + Bootstrap 5 (tema escuro) — página de chat, cards no dashboard, fila de sugestões nas Transações, seção de IA em Configurações. Novo namespace i18n `ai` (pt-BR/en).
- **Notificações:** reaproveita `INotificationChannel` (Telegram/e-mail) já existente.
- **Deploy:** mesmo Docker Compose na VPS; migração pgvector automática no start.

---

## 2. Objetivo & métricas de sucesso

### 2.1 Objetivo principal
- Reduzir drasticamente transações "Sem categoria" sem o usuário digitar nada.
- Perguntar ao próprio dinheiro em português e receber respostas exatas ("quanto gastei com iFood em maio?").
- Ser avisado proativamente de cobrança estranha, assinatura que subiu, gasto atípico.
- Ver para onde o mês está indo (projeção) e um resumo escrito no fim do mês.

### 2.2 Proposta de valor
"O SimpleFinance já mostra tudo. Agora ele **entende** e **explica** — e ainda organiza sozinho o que antes ficava sem categoria."

### 2.3 Diferencial principal
Apps BR (Mobills, Organizze, Meu Dinheiro) são de lançamento manual e têm pouca ou nenhuma IA conversacional sobre Open Finance. Os players com assistente de IA (Cleo, Copilot, Monarch, Origin) são SaaS US$/mês, fechados e com dados na nuvem deles. Aqui a IA roda **na sua infra**, sobre dados que **já chegam sozinhos** via Pluggy, com **custo marginal de centavos** (DeepSeek) e **guardrails de privacidade** (nunca envia número de conta/cartão ao LLM).

### 2.4 Métricas de sucesso
- **North Star:** % de transações categorizadas (regra+IA) por mês. **Alvo: ≥ 90%** (hoje ~7%).
- Precisão da categorização auto (amostra revisada pelo usuário) — **alvo ≥ 90%** de acerto entre as auto-aplicadas.
- Custo mensal de IA — **alvo < US$ 2/mês** em uso pessoal típico (teto configurável).
- Latência de resposta do assistente — **p95 < 6 s** (primeira token < 2 s em streaming).
- Anomalias úteis: taxa de "dispensar sem ação" < 40% (sinal de ruído baixo).

---

## 3. Público-alvo
### 3.1 Cliente ideal
O próprio dono do SimpleFinance — single-user, auto-hospedado. Não há multiusuário nesta camada.

### 3.2 Usuários/papéis
- **Usuário único** (dono): usa todas as features de IA.
- **Jobs do sistema** (Hangfire): produtores de embeddings, sugestões, insights, previsão, resumo.

### 3.3 Quem paga vs quem usa
Mesma pessoa. Custo = chaves DeepSeek + OpenAI (pré-pago), sob teto mensal.

---

## 4. Modelo de negócio
Sem monetização. Ferramenta pessoal. "Plano" = suas próprias chaves de API. Custo controlado por `Ai:MonthlyTokenBudgetUsd`.

---

## 5. Escopo do MVP

### 5.1 Módulos incluídos no MVP (camada IA)
1. **Categorização inteligente** — pipeline regras → embeddings (similaridade) → LLM (fallback); híbrido por confiança (auto se alta, fila de sugestão se baixa).
2. **Assistente conversacional** — chat PT-BR com tool-calling (Q&A exato) + RAG (busca semântica); pode **propor ações** (regra, orçamento, meta, recategorização) que o usuário confirma antes de gravar.
3. **Insights automáticos** — cards narrativos no dashboard (variações de gasto, tendências, assinaturas).
4. **Detecção de anomalias** — estatística (z-score/rolling) detecta; LLM explica em PT-BR; vira notificação.
5. **Previsão de fluxo/saldo** — baseline determinístico (recorrentes + médias históricas) + narrativa da IA.
6. **Resumo mensal por IA** — texto no fim do mês (para onde foi o dinheiro, o que mudou, sugestões), no app + Telegram/e-mail.
7. **Guardrails & custo** — teto mensal de tokens, redação de PII, cache de embeddings, toggle global e por-feature, log de uso/custo.

### 5.2 Fora do MVP
- Ações **automáticas sem confirmação** pelo assistente (todo write pede OK).
- Execução/iniciação de pagamento por IA (segue fora, como no PRD base).
- Conselho de investimento personalizado ("compre X") — **proibido**; a IA descreve, não recomenda alocação (ver §14).
- Fine-tuning de modelo próprio; treinamento com seus dados.
- Voz / STT-TTS.
- Multiusuário / perfis de IA por pessoa.
- Provider local/Ollama (arquitetura permite, mas não implementado no MVP).
- Categorização com split de 1 transação em várias categorias (segue dependente de `TransactionSplit`, ainda não implementado).

### 5.3 Arquitetura prepara para depois
- **Troca de provider**: `ILlmClient`/`IEmbeddingClient` permitem plugar OpenAI, Anthropic ou Ollama local sem tocar no domínio (mesma filosofia do `IFinancialDataProvider`).
- **Ações autônomas**: o catálogo de tools de ação já separa "propor" de "executar" — habilitar auto-exec é um flag futuro.
- **Split por IA**: categorização já grava `Confidence` e `Source`; quando `TransactionSplit` existir, o pipeline aceita múltiplos alvos.
- **Multi-provider de embeddings**: `EmbeddingModel` gravado por vetor permite reindexar ao trocar de modelo.

---

## 6. Requisitos funcionais

Códigos `RF-AI-*`. Assumem o domínio base pronto (Transaction, Category, CategoryRule, Budget, Goal, RecurringTransaction, NetWorthSnapshot, Notification).

### 6.1 Infra de IA & Provider
**RF-AI-001 — Abstração de LLM**
`ILlmClient` com: `ChatAsync(messages, tools?, model?, ct)` retornando conteúdo + tool-calls + uso de tokens; suporte a streaming (`ChatStreamAsync`). Impl `DeepSeekLlmClient` (OpenAI-compatível). Modelo default `deepseek-chat`; `deepseek-reasoner` selecionável por feature.
Aceite: chamada de teste retorna texto e contabiliza `PromptTokens`/`CompletionTokens` em `AiUsageLog`.

**RF-AI-002 — Abstração de embeddings**
`IEmbeddingClient.EmbedAsync(texts[], ct) -> float[][]`. Impl `OpenAiEmbeddingClient` (`text-embedding-3-small`, 1536). Cache: não reembeddar texto idêntico já vetorizado (hash do texto normalizado).
Aceite: 100 descrições retornam 100 vetores 1536-dim; segunda chamada idêntica não gera custo (cache hit).

**RF-AI-003 — Resiliência e custo por chamada**
Todas as chamadas via `HttpClient` com `AddStandardResilienceHandler` (retry/backoff/circuit-breaker, já padrão do projeto). Cada chamada grava `AiUsageLog` (feature, modelo, tokens in/out, custo estimado USD).

**RF-AI-004 — Teto de gasto mensal (kill-switch)**
Antes de qualquer chamada cloud, checar gasto do mês vs `UserSettings.AiMonthlyBudgetUsd`. Se estourar: (a) bloquear novas chamadas cloud, (b) categorização cai para **só regras**, (c) assistente responde "orçamento de IA do mês atingido", (d) dispara 1 notificação. Reseta no dia 1.
Aceite: com teto = 0, nenhuma chamada cloud é feita e o app segue funcional sem IA.

**RF-AI-005 — Toggle global e por-feature**
`UserSettings` guarda `AiEnabled` (mestre) + flags por feature (categorização, assistente, insights, anomalias, previsão, resumo). Feature off ⇒ job/endpoint correspondente vira no-op.

### 6.2 Categorização inteligente
**RF-AI-010 — Pipeline em cascata**
Para transação sem categoria (ou recém-chegada), aplicar nesta ordem, parando no primeiro êxito confiante:
1. **Regras do usuário** (`CategoryRuleEngine` existente) → `CategorySource=Rule`.
2. **Mapeamento Pluggy** (`CategoryMapping`) → `CategorySource=SystemMapping`.
3. **Similaridade por embedding**: vetoriza `Description`+`MerchantName`; busca kNN entre transações já categorizadas do usuário (mesmo por regra/override); se o vizinho mais próximo passa do limiar e tem maioria de classe consistente → categoria por voto kNN.
4. **LLM (fallback)**: se ainda incerto, `deepseek-chat` recebe {descrição, merchant, valor, categoria Pluggy, lista de categorias do usuário} e retorna `{categoryId, confidence, rationale}`.
Aceite: transação "IFD*IFOOD" sem regra é classificada como Alimentação por embedding/LLM com `Confidence` gravado.

**RF-AI-011 — Híbrido por confiança**
`Confidence ≥ Ai.AutoApplyThreshold` (default 0.85) ⇒ aplica automático (`CategorySource=AiAuto`) e registra em log revisável. `Confidence < limiar` ⇒ cria `AiSuggestion` pendente (não altera a transação).
Aceite: mudar o limiar para 0.99 faz quase tudo virar sugestão em vez de auto.

**RF-AI-012 — Nunca sobrescrever escolha humana**
Transação com `CategorySource=UserOverride` (ou `Rule` do usuário) é imutável pela IA (generaliza a lição de proveniência do PITFALLS #10 já citada no código). Re-sync/re-categorização em lote respeitam isso.

**RF-AI-013 — Fila de sugestões (revisão)**
Endpoint lista `AiSuggestion` pendentes com {transação, categoria sugerida, confiança, motivo}. Ações: aceitar (aplica, `CategorySource=UserOverride`, vira exemplo positivo p/ kNN), rejeitar (marca, opcionalmente vira exemplo negativo), aceitar em lote.
Aceite: aceitar uma sugestão categoriza a transação e a remove da fila.

**RF-AI-014 — Aprender vira regra (opcional)**
Ao aceitar uma sugestão recorrente para o mesmo merchant, oferecer "criar regra: merchant = X → categoria Y" (usa o motor de regras existente). Um clique cria a `CategoryRule`.

**RF-AI-015 — Recategorização em lote via IA**
Botão "categorizar tudo que falta com IA" dispara `AiCategorizationJob` sob demanda para o backlog de "Sem categoria", respeitando teto e limiar.

### 6.3 Assistente conversacional
**RF-AI-020 — Chat com histórico**
Endpoint de chat (streaming) sobre `AiConversation`/`AiMessage`. Mantém contexto da conversa; lista/renomeia/apaga conversas. Idioma de resposta segue i18n (default PT-BR).

**RF-AI-021 — Tool-calling (leitura, números exatos)**
LLM dispõe de um catálogo de tools **read-only** que executam consultas parametrizadas/serviços existentes e devolvem só o agregado (nunca despejam o histórico bruto):
- `query_transactions(period, category?, merchant?, min?, max?, type?)`
- `sum_by_category(period, type)`
- `budget_status(month)`
- `net_worth(asOf?)` / `net_worth_evolution(range)`
- `investments_summary()`
- `list_recurring(activeOnly?)`
- `search_transactions_semantic(text, topK)` ← via embeddings (RAG)
Aceite: "quanto gastei com Alimentação em maio?" chama `sum_by_category` e responde o valor exato em BRL, não uma estimativa.

**RF-AI-022 — Tools de ação (com confirmação obrigatória)**
Catálogo **write** que o LLM só pode **propor**: `create_category_rule`, `set_budget`, `create_goal`, `recategorize_transactions`. O LLM retorna uma *proposta estruturada*; o backend **não executa** — devolve um "card de confirmação" ao front. Só um segundo request explícito do usuário (`POST /ai/actions/{id}/confirm`) grava.
Aceite: "cria uma regra pra tudo do iFood virar Alimentação" gera um card; nada é gravado até eu clicar Confirmar.

**RF-AI-023 — Fundamentação (grounding) e honestidade**
Respostas numéricas devem vir de tools, não de "chute" do modelo. Se a pergunta exige dado que nenhuma tool cobre, o assistente diz que não sabe em vez de inventar. Não dá conselho de investimento (ver §14).

**RF-AI-024 — Limites de contexto/custo**
Janela de conversa truncada por tokens; resultados de tools resumidos; usa `deepseek-chat` por padrão. Cada mensagem grava uso em `AiUsageLog` e respeita o teto.

### 6.4 Insights automáticos
**RF-AI-030 — Geração de insights**
`AiInsightJob` (diário/pós-sync) calcula sinais determinísticos (variação MoM por categoria, novos merchants relevantes, % do orçamento consumido, tendência de saldo) e pede ao LLM títulos/textos curtos em PT-BR. Grava `AiInsight`.
Tipos: `SpendingChange`, `Trend`, `SubscriptionCreep`, `BudgetRisk`.
Aceite: se Alimentação sobe >25% vs média de 3 meses, aparece card "Alimentação subiu 30% este mês".

**RF-AI-031 — Superfície e ciclo de vida**
Cards no dashboard; ações Ver/Dispensar. `Status`: New→Seen→Dismissed. Não repetir o mesmo insight já dispensado no período.

### 6.5 Detecção de anomalias
**RF-AI-040 — Detecção estatística**
`AiAnomalyJob` (pós-sync + noturno) sinaliza: valor muito acima do histórico do merchant (z-score), possível cobrança **duplicada** (mesmo merchant+valor em janela curta), **assinatura que aumentou** (recorrente com valor maior que o padrão), gasto atípico de categoria. Detecção é **determinística**; LLM só redige a explicação.
Aceite: assinatura de streaming que passa de R$ 20 para R$ 35 gera anomalia "Assinatura X aumentou 75%".

**RF-AI-041 — Virar alerta/notificação**
Anomalia de severidade alta cria `Notification` e, se configurado, empurra Telegram/e-mail via `NotificationDispatcher`. Reaproveita o modelo de alertas existente.

**RF-AI-042 — Sem falso-positivo repetido**
Anomalia dispensada não repete para a mesma ocorrência; padrões recorrentes legítimos (ex.: salário) entram numa allowlist implícita por histórico.

### 6.6 Previsão de fluxo/saldo
**RF-AI-050 — Projeção determinística**
`AiForecastJob` (diário) projeta, para o mês corrente e o próximo: receita esperada, despesa esperada e **saldo projetado**, combinando recorrentes detectadas + média histórica por categoria + transações já ocorridas no mês. Grava `Forecast`. **O número não vem do LLM.**
Aceite: no dia 10, a projeção de saldo do fim do mês bate razoavelmente com recorrentes+média (documentar método).

**RF-AI-051 — Narrativa opcional**
LLM gera 1-2 frases explicando a projeção ("no ritmo atual, você fecha o mês ~R$ 300 abaixo do orçamento de Lazer"). Texto é comentário; o valor é o determinístico.

**RF-AI-052 — Exibição**
Widget no dashboard e/ou em Relatórios: saldo projetado + faixa (min/max) + principais direcionadores.

### 6.7 Resumo mensal por IA
**RF-AI-060 — Geração mensal**
`AiMonthlySummaryJob` (dia 1, sobre o mês anterior) monta um pacote de fatos (totais por categoria, variações, top merchants, orçamento vs real, patrimônio, assinaturas novas/alteradas) e pede ao **`deepseek-reasoner`** um resumo em prosa PT-BR com 3-5 destaques + sugestões acionáveis. Grava `AiInsight` tipo `MonthlySummary`.
Aceite: no dia 1, existe um resumo do mês anterior no app.

**RF-AI-061 — Entrega**
Disponível em Relatórios/dashboard e empurrado por Telegram/e-mail (se canal ativo). Um resumo por mês (idempotente).

### 6.8 Configurações & Uso
**RF-AI-070 — Tela de configurações de IA**
Editar: toggle mestre + por-feature, limiar de auto-categorização, teto mensal (USD), modelo preferido por feature, chaves presentes? (status, nunca exibe a chave). 
**RF-AI-071 — Painel de uso/custo**
Gasto do mês (tokens e USD estimado) por feature, com barra vs teto. Histórico simples (`AiUsageLog`).

---

## 7. Requisitos não funcionais

### 7.1 Performance
- Assistente em **streaming**: primeira token < 2 s (p95), resposta completa p95 < 6 s.
- Categorização em lote: ≥ 500 transações/execução sem travar o app (job assíncrono, batelado).
- Embedding: em lotes de até 100 textos por request.

### 7.2 Segurança & privacidade (crítico)
- **Nunca** enviar ao LLM/embeddings: número de conta, número/dígitos de cartão, `MerchantDocument` (CPF/CNPJ), tokens Pluggy. Enviar só `Description`, `MerchantName`, `Amount`, categoria. **Camada de redação obrigatória** antes de sair da VPS.
- Chaves (DeepSeek, OpenAI) só em env var; status exibível, valor nunca.
- Prompt-injection: conteúdo de transação (descrições vindas do banco) é **dado, não instrução**. Tools nunca executam texto livre; ações sempre exigem confirmação humana explícita.
- Tools read-only são consultas parametrizadas escopadas ao `UserId` — LLM nunca compõe SQL cru.
- Log de uso não guarda o conteúdo das transações enviadas além do necessário para depurar (configurável).

### 7.3 Custo (política "caro→barato" — decisão do usuário)
- `deepseek-chat` como padrão em tudo; `deepseek-reasoner` só no resumo mensal e planejamento de ação.
- Batch **noturno** para embeddings/categorização/anomalias (evita picos e reprocesso).
- Cache de embeddings; não reprocessa texto idêntico.
- Teto mensal **baixo por padrão** (`Ai:MonthlyTokenBudgetUsd` default sugerido US$ 3) com kill-switch.
- Truncamento agressivo de contexto no chat.

### 7.4 Responsividade
Chat, cards e fila de sugestões responsivos, tema escuro. i18n pt-BR/en.

### 7.5 Disponibilidade & operação
- Migração pgvector idempotente no start (`Database.Migrate()` + `CREATE EXTENSION IF NOT EXISTS vector`).
- IA é **degradável**: se DeepSeek/OpenAI caem ou teto estoura, o app continua 100% funcional sem IA (categorização só por regras, chat indisponível com aviso claro).

### 7.6 Observabilidade
Serilog com correlação por feature/job. Métricas: tokens/custo por dia, taxa de auto-aplicação, aceite/rejeição de sugestões, latência do chat.

### 7.7 Confiabilidade da categorização
Toda categoria atribuída por IA grava `Confidence` + `Source` + timestamp, e é auditável/reversível (desfazer volta para "Sem categoria" ou categoria anterior).

---

## 8. UX & interface

### 8.1 Layout principal
Mantém sidebar/topbar atuais. Novos pontos:
- Item de menu **"Assistente"** (ícone chat).
- **Cards de IA** no topo do Dashboard (insights + anomalias + saldo projetado).
- **Aba/filtro "Sugestões da IA"** dentro de Transações.
- Seção **"IA"** em Configurações + painel de **Uso**.

### 8.2 Tema
Tema escuro (existente). Namespace i18n novo: `ai`.

### 8.3 Dashboard inicial (novos elementos)
Faixa de IA no topo: 1-3 insights, anomalias críticas, widget de saldo projetado. Colapsável. Some se `AiEnabled=false`.

### 8.4 Experiência priorizada
1. Chegar no app e ver "% categorizado" subir sozinho + fila de sugestões pequena p/ revisar.
2. Perguntar no chat e receber número exato.
3. Ser avisado de anomalia sem procurar.

---

## 9. Fluxo de navegação

### 9.1 Árvore de menu (adições)
```
Sidebar (existente) +:
  Assistente            → /assistente (chat)
Dashboard (existente):
  + faixa de cards de IA (insights/anomalias/previsão)
Transações (existente):
  + filtro "Sugestões da IA" + ação "Categorizar faltantes com IA"
Configurações (existente):
  + IA (toggles, limiar, teto, modelos)
  + Uso de IA (tokens/custo)
Relatórios (existente):
  + Resumo mensal por IA
```

### 9.2 Fluxo — Categorização passiva (dia a dia)
Sync traz transações → `AiEmbeddingJob` vetoriza → `AiCategorizationJob` roda cascata → confiança alta auto-aplica, baixa vira sugestão → usuário revê fila quando quiser.

### 9.3 Fluxo — Perguntar (assistente)
Usuário digita → LLM escolhe tool(s) read → backend executa escopado ao UserId → LLM redige resposta com o número exato → streaming na tela.

### 9.4 Fluxo — Ação via chat (com confirmação)
Usuário pede ação → LLM propõe (tool write) → backend devolve card → usuário confirma → backend executa (cria regra/orçamento/meta/recategoriza) → confirma na conversa.

### 9.5 Fluxo — Anomalia
`AiAnomalyJob` detecta → cria `Notification` + `AiInsight` → Telegram/e-mail (se ativo) → card no dashboard → usuário Vê/Dispensa.

### 9.6 Fluxo — Resumo mensal
Dia 1 → `AiMonthlySummaryJob` monta fatos → `deepseek-reasoner` escreve → grava + entrega push.

---

## 10. Telas do MVP

- **Assistente (`/assistente`)**: lista de conversas (nova/renomear/apagar), thread com streaming, "chips" de tool usada (transparência: "consultei sum_by_category"), card de confirmação de ação, indicador de custo/estado do teto.
- **Dashboard — faixa de IA**: cards de insight (título+texto+ação), cards de anomalia (severidade+dispensar), widget de saldo projetado (valor+faixa+drivers).
- **Transações — Sugestões da IA**: tabela {transação, categoria sugerida, confiança, motivo}, aceitar/rejeitar/aceitar-lote, botão "Categorizar faltantes com IA", oferta "virar regra".
- **Configurações — IA**: toggle mestre + por-feature, slider de limiar, teto mensal (USD), seleção de modelo por feature, status das chaves.
- **Configurações — Uso de IA**: gasto do mês (tokens/USD) por feature, barra vs teto, histórico.
- **Relatórios — Resumo mensal**: prosa do mês + destaques + sugestões; botão reenviar ao Telegram.

---

## 11. Modelo de dados

Novas entidades no domínio (`UserId` em todas; `UserOwnedEntity` como base, igual ao resto). Enum estendido.

### 11.1 Extensão de enum existente
```
CategorySource (existente): SystemMapping | Rule | UserOverride
  + AiAuto        # aplicado automaticamente pela IA (confiança alta)
  + AiSuggested   # proposto pela IA e aceito pelo usuário (equivale a override, mas rastreia origem IA)
```
> `Transaction` ganha (opcional) `CategoryConfidence` (double?) e `CategoryModel` (string?) para auditar a origem IA sem tabela nova.

### 11.2 Embeddings & similaridade
```
TransactionEmbedding:
- Id, UserId
- TransactionId (FK, unique)
- Embedding  vector(1536)          # pgvector
- EmbeddingModel                    # ex.: text-embedding-3-small
- SourceTextHash                    # p/ cache/dedup
- CreatedAt
# Índice ivfflat/hnsw em Embedding p/ kNN
```

### 11.3 Sugestões da IA
```
AiSuggestion:
- Id, UserId
- Type (Category|Rule|Budget|Goal)
- TransactionId (nullable)
- SuggestedPayload (jsonb)          # ex.: { categoryId, ... }
- Confidence (double)
- Rationale (text)
- Source (Embedding|Llm|Statistical)
- Status (Pending|Accepted|Rejected|AutoApplied|Expired)
- CreatedAt, DecidedAt
```

### 11.4 Insights & anomalias & previsão
```
AiInsight:
- Id, UserId
- Kind (SpendingChange|Trend|SubscriptionCreep|BudgetRisk|Anomaly|MonthlySummary|Forecast)
- Severity (Info|Warning|Critical)
- Title, Body (text)               # gerados/curados pela IA
- PeriodStart, PeriodEnd
- DataJson (jsonb)                  # números determinísticos que embasam o texto
- Status (New|Seen|Dismissed)
- DedupKey                         # evita repetir o mesmo insight
- CreatedAt

Forecast:
- Id, UserId
- Month (date, 1º dia)
- ProjectedIncome, ProjectedExpense, ProjectedBalance (decimal)
- BalanceLow, BalanceHigh (decimal)      # faixa
- ByCategoryJson (jsonb)
- Method                                 # ex.: recurring+3mo-avg
- Narrative (text, nullable)
- CreatedAt
```

### 11.5 Conversas do assistente
```
AiConversation:
- Id, UserId
- Title
- CreatedAt, UpdatedAt

AiMessage:
- Id, ConversationId, UserId
- Role (User|Assistant|Tool)
- Content (text)
- ToolName (nullable), ToolCallsJson (jsonb, nullable), ToolResultJson (jsonb, nullable)
- Model (nullable)
- PromptTokens, CompletionTokens (int, nullable)
- CreatedAt

AiPendingAction:                     # ação proposta aguardando confirmação
- Id, UserId, ConversationId (nullable)
- ActionType (CreateRule|SetBudget|CreateGoal|Recategorize)
- Payload (jsonb)
- Status (Pending|Confirmed|Cancelled|Expired)
- CreatedAt, DecidedAt
```

### 11.6 Uso & custo
```
AiUsageLog:
- Id, UserId
- Feature (Categorization|Assistant|Insights|Anomaly|Forecast|MonthlySummary|Embedding)
- Provider (DeepSeek|OpenAI)
- Model
- PromptTokens, CompletionTokens (int)
- EstimatedCostUsd (decimal)
- CreatedAt
```

### 11.7 Configurações (campos novos em `UserSettings`)
```
UserSettings (+):
- AiEnabled (bool)                          # mestre
- AiCategorizationEnabled, AiAssistantEnabled, AiInsightsEnabled,
  AiAnomalyEnabled, AiForecastEnabled, AiMonthlySummaryEnabled (bool)
- AiAutoApplyThreshold (double, default 0.85)
- AiMonthlyBudgetUsd (decimal, default 3.00)
- AiChatModel (string, default "deepseek-chat")
- AiSummaryModel (string, default "deepseek-reasoner")
- AiLanguage (string, default segue i18n)
```

---

## 12. Backend

### 12.1 Stack
.NET 10, ASP.NET Core Minimal APIs, EF Core (+pgvector), Dapper (agregados p/ tools), Hangfire, Serilog, `Microsoft.Extensions.Http.Resilience`. Sem SDK proprietário de LLM — cliente `HttpClient` tipado (DeepSeek é OpenAI-compatível; OpenAI só p/ embeddings).

### 12.2 Arquitetura
Novo módulo alinhado ao monólito modular existente:
```
SimpleFinance.Application/Ai/
  ILlmClient.cs, IEmbeddingClient.cs
  Categorization/  (AiCategorizationService, cascata + confiança)
  Assistant/       (tool catalog: read + write; ToolExecutor)
  Insights/, Anomaly/, Forecast/, Summary/
  Usage/           (budget guard, cost calc)
SimpleFinance.Infrastructure/Ai/
  DeepSeek/DeepSeekLlmClient.cs
  OpenAi/OpenAiEmbeddingClient.cs
  Redaction/PiiRedactor.cs
  Jobs/  (AiEmbeddingJob, AiCategorizationJob, AiAnomalyJob, AiForecastJob, AiMonthlySummaryJob, AiInsightJob)
  AiServiceCollectionExtensions.cs
SimpleFinance.Api/Endpoints/AiEndpoints.cs
```
Reaproveita: `CategoryRuleEngine`, `CategorySeeder`, `NotificationDispatcher`, `IDbConnectionFactory`, `IFinancialDataProvider` (não muda), jobs Hangfire existentes (agenda os novos junto).

### 12.3 Módulos de domínio (IA)
Categorization · Assistant (tools) · Insights · Anomaly · Forecast · Summary · Usage/Budget · Embeddings.

### 12.4 Endpoints (REST) — exemplos
```
# Assistente
POST   /api/ai/chat                      # streaming (SSE); body: { conversationId?, message }
GET    /api/ai/conversations
GET    /api/ai/conversations/{id}
PATCH  /api/ai/conversations/{id}        # renomear
DELETE /api/ai/conversations/{id}
POST   /api/ai/actions/{id}/confirm      # executa ação proposta
POST   /api/ai/actions/{id}/cancel

# Categorização & sugestões
GET    /api/ai/suggestions?status=pending
POST   /api/ai/suggestions/{id}/accept
POST   /api/ai/suggestions/{id}/reject
POST   /api/ai/suggestions/bulk-accept
POST   /api/ai/categorize/run            # dispara AiCategorizationJob sob demanda

# Insights / anomalias / previsão / resumo
GET    /api/ai/insights?status=new
POST   /api/ai/insights/{id}/dismiss
GET    /api/ai/forecast?month=YYYY-MM
GET    /api/ai/summary?month=YYYY-MM
POST   /api/ai/summary/{id}/resend       # reenvia ao Telegram/e-mail

# Config & uso
GET    /api/ai/settings
PUT    /api/ai/settings
GET    /api/ai/usage?month=YYYY-MM
```
Todos autenticados (JWT existente), escopados ao `UserId`. Validação FluentValidation (manual, padrão do projeto).

### 12.5 Jobs Hangfire (recorrências sugeridas)
```
AiEmbeddingJob        # noturno + pós-sync (só novas transações)
AiCategorizationJob   # noturno + sob demanda
AiAnomalyJob          # pós-sync + noturno
AiInsightJob          # diário
AiForecastJob         # diário
AiMonthlySummaryJob   # mensal (dia 1)
```
Worker count baixo (já é a orientação do projeto p/ VPS pequena). Todos checam teto antes de chamar cloud.

---

## 13. Frontend

### 13.1 Stack
React 19 + Vite + TS + Bootstrap 5 + TanStack Query + react-hook-form + zod + react-i18next.

### 13.2 Estrutura (features/)
```
frontend/src/features/ai/
  api.ts                     # hooks: useChat (stream), useSuggestions, useInsights, useForecast, useAiSettings, useAiUsage
  AssistantPage.tsx          # /assistente
  components/
    ChatThread.tsx, ChatComposer.tsx, ToolChip.tsx, ActionConfirmCard.tsx
    InsightCard.tsx, AnomalyCard.tsx, ForecastWidget.tsx
    SuggestionQueue.tsx      # embutido em Transações
    AiSettingsSection.tsx, AiUsagePanel.tsx
  i18n: locales/{pt-BR,en}/ai.json
```
Streaming via `fetch`+`ReadableStream` (SSE) no `useChat`. Cards de IA no Dashboard atrás de `AiEnabled`.

### 13.3 Componentes-base reutilizados
Card/Badge/Modal/Table (react-bootstrap); `ChartCard` (já corrigido de flicker) p/ forecast; toasts para aceite/rejeição.

---

## 14. Regras de negócio importantes (invariantes)

1. **Escopo por usuário**: toda query/tool filtra por `UserId` (single-user, mas mantém a invariante do PRD base).
2. **Redação de PII obrigatória**: nenhuma chamada a LLM/embeddings inclui conta, cartão, `MerchantDocument` ou tokens. Passa sempre por `PiiRedactor`.
3. **IA não sobrescreve humano**: `UserOverride`/`Rule` são imutáveis para a IA.
4. **Write só com confirmação**: nenhuma tool de ação grava sem `POST /ai/actions/{id}/confirm`.
5. **Números vêm de tools**, não do modelo: valores monetários exibidos ao usuário são calculados por consulta, nunca "gerados" pelo LLM.
6. **Sem conselho de investimento**: a IA descreve/organiza ("você tem X em renda fixa"), mas **não recomenda comprar/vender/alocar**. Se perguntado, informa que não é consultor licenciado. (Alinha com a política de não dar conselho financeiro personalizado.)
7. **Teto de custo é hard-limit**: estourou o mês ⇒ zero chamadas cloud até virar o mês.
8. **Degradação graciosa**: falha/indisponibilidade de IA nunca quebra fluxo core (sync, dashboard, transações).
9. **Idempotência**: resumo mensal e insights usam `DedupKey`/período; reprocesso não duplica.
10. **Auditabilidade**: categoria por IA guarda `Confidence`+`Model`+`Source` e é reversível.
11. **Provider trocável**: domínio depende só de `ILlmClient`/`IEmbeddingClient`.

---

## 15. Roadmap de implementação

**Fase A — Fundação IA & custo** (base p/ tudo)
- pgvector + migração; `ILlmClient` (DeepSeek) + `IEmbeddingClient` (OpenAI); `AiUsageLog`; budget guard/kill-switch; `PiiRedactor`; campos em `UserSettings`; endpoints/tela de settings + uso.

**Fase B — Categorização inteligente**
- `TransactionEmbedding` + `AiEmbeddingJob`; cascata regras→kNN→LLM; confiança/auto vs sugestão; `AiSuggestion`; fila de sugestões no front; recategorização em lote; "virar regra".

**Fase C — Assistente conversacional**
- Conversas/mensagens; tools read (tool-calling) + `search_transactions_semantic` (RAG); streaming; tools write + confirmação (`AiPendingAction`); página `/assistente`.

**Fase D — Proativo: insights & anomalias**
- `AiInsightJob` + `AiAnomalyJob`; cards no dashboard; integração com `NotificationDispatcher` (Telegram/e-mail).

**Fase E — Previsão & resumo mensal**
- `Forecast` + `AiForecastJob` (determinístico) + narrativa; widget; `AiMonthlySummaryJob` (reasoner) + entrega push; tela em Relatórios.

**Fase F — Polimento & guardrails**
- i18n `ai` completo; testes (unit da cascata, budget guard, redação; e2e do chat/confirmação); ajuste de limiares; documentação de custo/privacidade.

---

## 16. Prompts para vibecode (ordem de implementação)

> Cada prompt assume o SimpleFinance atual (repo existente, .NET 10 + React + Postgres + Hangfire) e os anteriores prontos. Executar via GSD (o projeto já usa) ou colar num agente de código.

### Prompt 1 — Fundação IA (provider, custo, settings)
Adicionar módulo de IA ao SimpleFinance. Criar `ILlmClient` e `IEmbeddingClient` em `SimpleFinance.Application/Ai`, com impls em `Infrastructure/Ai`: `DeepSeekLlmClient` (HttpClient tipado, base `https://api.deepseek.com`, OpenAI-compatível, `AddStandardResilienceHandler`, modelos `deepseek-chat`/`deepseek-reasoner`, suporte a tool-calling e streaming) e `OpenAiEmbeddingClient` (`text-embedding-3-small`, 1536, batelado, cache por hash de texto). Habilitar pgvector no Postgres (`CREATE EXTENSION IF NOT EXISTS vector` na migração). Criar `AiUsageLog` + serviço de custo/estimativa e um **budget guard** que bloqueia chamadas cloud quando o gasto do mês excede `UserSettings.AiMonthlyBudgetUsd`. Adicionar `PiiRedactor` (remove conta/cartão/MerchantDocument/tokens). Estender `UserSettings` com os campos de IA (§11.7). Endpoints `GET/PUT /api/ai/settings` e `GET /api/ai/usage`. Chaves via env (`Ai:DeepSeekApiKey`, `Ai:OpenAiApiKey`). Nada é enviado ao LLM sem passar por redação. Testes: budget guard corta em teto=0; redator remove PII.

### Prompt 2 — Categorização inteligente (embeddings + cascata + sugestões)
Criar `TransactionEmbedding` (pgvector 1536, índice kNN) e `AiEmbeddingJob` (Hangfire, noturno + pós-sync) que vetoriza transações novas (texto = Description+MerchantName, redigido). Implementar `AiCategorizationService` com cascata: (1) `CategoryRuleEngine` existente, (2) `CategoryMapping`, (3) similaridade kNN entre transações já categorizadas do usuário, (4) fallback `deepseek-chat` retornando `{categoryId, confidence, rationale}` dado o catálogo de categorias do usuário. Regra de confiança: ≥ `AiAutoApplyThreshold` aplica auto (`CategorySource=AiAuto`, grava `CategoryConfidence`/`CategoryModel`); abaixo cria `AiSuggestion` (Pending). Nunca tocar `UserOverride`/`Rule`. Estender enum `CategorySource` com `AiAuto`/`AiSuggested`. `AiCategorizationJob` (noturno + sob demanda via `POST /api/ai/categorize/run`). Endpoints de sugestões: listar pendentes, accept (aplica, vira exemplo positivo, `Source=AiSuggested`/override), reject, bulk-accept. Front: aba "Sugestões da IA" em Transações (tabela + aceitar/rejeitar/lote + botão "Categorizar faltantes"), oferta "criar regra deste merchant". Testes: transação iFood sem regra → Alimentação com confiança; sugestão de baixa confiança não altera a transação.

### Prompt 3 — Assistente conversacional (tool-calling + RAG + ações confirmadas)
Criar `AiConversation`/`AiMessage`/`AiPendingAction` e `AiEndpoints`: `POST /api/ai/chat` (SSE streaming), CRUD de conversas. Implementar catálogo de **tools read** (`query_transactions`, `sum_by_category`, `budget_status`, `net_worth`, `net_worth_evolution`, `investments_summary`, `list_recurring`, `search_transactions_semantic` via embeddings) como consultas parametrizadas escopadas ao `UserId` (Dapper p/ agregados). Implementar **tools write** (`create_category_rule`, `set_budget`, `create_goal`, `recategorize_transactions`) que **não executam**: geram `AiPendingAction` e retornam um card de confirmação; execução só em `POST /api/ai/actions/{id}/confirm`. Grounding: números só via tools; sem conselho de investimento; responde "não sei" quando fora do alcance das tools. Respeitar budget guard e truncamento de contexto (`deepseek-chat`). Front: página `/assistente` (lista de conversas, thread streaming, chips de tool, `ActionConfirmCard`), item de menu "Assistente". Testes: "quanto gastei com Alimentação em maio?" chama `sum_by_category` e devolve valor exato; ação proposta não grava sem confirmar.

### Prompt 4 — Insights & anomalias (proativo)
Criar `AiInsight` e jobs `AiInsightJob` (diário) e `AiAnomalyJob` (pós-sync + noturno). Insights: calcular sinais determinísticos (variação MoM por categoria, novos merchants, % orçamento, tendência de saldo) e pedir ao `deepseek-chat` títulos/textos curtos PT-BR; tipos `SpendingChange|Trend|SubscriptionCreep|BudgetRisk`. Anomalias: detecção **estatística** (z-score por merchant, duplicadas, assinatura que subiu, gasto atípico); LLM só redige explicação; severidade alta cria `Notification` e empurra Telegram/e-mail via `NotificationDispatcher`. Deduplicação por `DedupKey`; dispensado não repete. Front: faixa de cards de IA no Dashboard (`InsightCard`/`AnomalyCard`, Ver/Dispensar). Testes: gasto de categoria +30% gera card; assinatura 20→35 gera anomalia + notificação.

### Prompt 5 — Previsão & resumo mensal
Criar `Forecast` + `AiForecastJob` (diário): projeção **determinística** de receita/despesa/saldo do mês (recorrentes detectadas + média histórica 3m + realizado), com faixa min/max e drivers; narrativa opcional via `deepseek-chat` (texto, não número). `AiMonthlySummaryJob` (dia 1, mês anterior): montar pacote de fatos e pedir ao `deepseek-reasoner` resumo PT-BR com 3-5 destaques + sugestões; gravar `AiInsight` `MonthlySummary`; entregar no app + Telegram/e-mail; idempotente por mês. Front: `ForecastWidget` no Dashboard e seção "Resumo mensal" em Relatórios (com reenviar). Endpoints `GET /api/ai/forecast`, `GET /api/ai/summary`, `POST /api/ai/summary/{id}/resend`. Testes: projeção coerente com recorrentes; resumo do mês anterior existe no dia 1.

### Prompt 6 — Polimento, i18n e testes
Completar namespace i18n `ai` (pt-BR/en) em todas as telas novas. Cobrir com testes: cascata de categorização (cada nível), budget guard/kill-switch, `PiiRedactor`, confirmação de ação (write nunca grava sem confirm), streaming do chat (e2e), deduplicação de insights. Painel de uso com barra vs teto. Documentar no README/CLAUDE.md: variáveis de env de IA, política de custo "caro→barato", garantias de privacidade. Ajustar limiares default com base numa amostra real.

---

## 17. Critérios de sucesso do MVP
Ponta a ponta, o usuário consegue:
1. Ligar a IA em Configurações, ver as chaves reconhecidas e um teto mensal.
2. Sync trazer transações e o "% categorizado" subir sozinho, com uma fila pequena de sugestões p/ revisar em 1 clique.
3. Perguntar no chat ("quanto gastei com iFood em maio?", "sobrou quanto?") e receber número exato.
4. Pedir uma ação no chat ("cria regra iFood→Alimentação") e confirmá-la antes de gravar.
5. Ver no dashboard um insight e uma anomalia úteis, e receber a anomalia crítica no Telegram.
6. Ver o saldo projetado do mês e ler o resumo do mês anterior.
7. Se o teto estourar ou a IA cair, o app segue 100% funcional sem IA.

## 18. Prioridades de desenvolvimento
- **Alta (P0):** Fundação IA + custo/privacidade (Fase A); Categorização inteligente (Fase B). É o maior ganho (93%→90%) e a base.
- **Média (P1):** Assistente (Fase C); Insights & anomalias (Fase D).
- **Baixa (P2):** Previsão & resumo mensal (Fase E); polimento (Fase F).

## 19. Contexto de mercado & fontes
- **Tendência:** assistentes financeiros com LLM viraram padrão em fintechs US (Cleo, Copilot Money, Monarch, Origin); no BR o mercado ainda é dominado por apps de lançamento manual (Mobills, Organizze, Meu Dinheiro) com IA incipiente. Gap: IA conversacional sobre Open Finance auto-hospedado.
- **DeepSeek:** API OpenAI-compatível, com function-calling; `deepseek-chat` (V3) barato e `deepseek-reasoner` (R1) p/ raciocínio — encaixa na política de custo baixo. (Verificar preços/limites atuais em docs.deepseek.com no início da implementação.)
- **Embeddings:** OpenAI `text-embedding-3-small` (1536) é barato e forte em PT-BR; pgvector é o padrão de fato p/ kNN no Postgres.
- **Concorrentes (resumo):**

| Nome | Posicionamento | Preço | Forte | Fraco (p/ nosso caso) |
|---|---|---|---|---|
| Cleo | Assistente de IA "sassy" (US) | ~US$ 6/mês | Chat + coaching | US-only, dados na nuvem deles, sem Open Finance BR |
| Copilot Money | PFM premium com IA (US) | ~US$ 13/mês | UX + categorização | Fechado, SaaS, sem BR |
| Monarch/Origin | PFM + IA | ~US$ 100/ano | Consolidação + insights | SaaS, sem self-host, sem Pluggy |
| Mobills/Organizze | PFM BR | grátis/~R$ 20/mês | Mercado BR, categorias | Lançamento manual, IA fraca, dados na nuvem deles |

- **Oportunidade explorada:** IA rodando na infra do próprio usuário, sobre dados que chegam sozinhos via Pluggy, com custo de centavos e privacidade forte (PII nunca sai). Nenhum concorrente combina self-host + Open Finance BR + assistente LLM.
- **Fontes:** docs.deepseek.com (API/pricing/function-calling); platform.openai.com (embeddings); github.com/pgvector/pgvector; PRD-SimpleFinance.md; CLAUDE.md. *(Preços de concorrentes são ordens de grandeza públicas — reconfirmar antes de citar como fato.)*

---

## Conclusão
SimpleFinance IA transforma o app de "mostra tudo" em "entende e explica". O núcleo é a **categorização em cascata** (regras→embeddings→LLM, híbrida por confiança) que resolve o problema real dos 93% sem categoria sem digitação; em volta, um **assistente que responde com números exatos** (tool-calling) e **age só com confirmação**, mais uma camada **proativa** (insights, anomalias, previsão determinística e resumo mensal escrito por IA). Tudo atrás de abstrações trocáveis (`ILlmClient`/`IEmbeddingClient`), barato por design (DeepSeek + embeddings OpenAI + teto mensal), privado (PII nunca sai da VPS) e degradável (a IA cai, o app continua). Fluxo ponta-a-ponta: dados chegam via Pluggy → jobs vetorizam e categorizam → o usuário revisa uma fila mínima, pergunta em português, é avisado de anomalias e lê o resumo do mês — sem nunca lançar uma transação à mão.
