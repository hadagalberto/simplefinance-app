# SimpleFinance

App de finanças pessoais auto-hospedado e de usuário único. Conecta às suas contas bancárias,
cartões e investimentos via **Pluggy (Open Finance Brasil)**, consolida tudo numa visão única e
ajuda a entender os gastos, seguir um orçamento, acompanhar o patrimônio, detectar assinaturas
recorrentes, bater metas e receber alertas. A fonte de verdade é o que os bancos retornam via
Pluggy — não há lançamento manual de transações.

Especificação completa e canônica: [`docs/PRD-SimpleFinance.md`](docs/PRD-SimpleFinance.md). A
especificação da camada de IA está em [`docs/PRD-SimpleFinance-IA.md`](docs/PRD-SimpleFinance-IA.md).

## Stack

- **Backend:** .NET 10 (ASP.NET Core), EF Core (domínio/CRUD) + Dapper (relatórios), Hangfire (jobs), PostgreSQL 17 + pgvector.
- **Frontend:** React 19 + Vite + TypeScript, TanStack Query, ApexCharts, react-router 7, Bootstrap 5 (veja [Frontend](#frontend)).
- **Deploy:** Docker Compose numa VPS, reverse proxy Caddy (HTTPS automático), migrations aplicadas automaticamente no start da API.

## Primeiros passos

1. `cp .env.example .env` e preencha os valores reais (veja os comentários em `.env.example`).
2. `docker compose up -d` — a API aplica as migrations do EF ao iniciar.
3. O frontend é servido na origem configurada em `Cors__AllowedOrigin` / `VITE_API_BASE_URL`.

Nunca faça commit do `.env` — ele está no `.gitignore`. Todos os segredos (client/secret da
Pluggy, chave de assinatura do JWT, credenciais de Telegram/SMTP, chaves dos provedores de IA)
ficam só em variáveis de ambiente, nunca no banco.

## Frontend

O cliente web fica em `frontend/`.

- **Stack:** React 19 + Vite + TypeScript; Bootstrap 5 e react-bootstrap via npm; tema SCSS
  próprio em `frontend/src/styles/` (escuro por padrão, claro disponível na topbar); ApexCharts;
  TanStack Query; react-hook-form + zod; i18n em pt-BR (padrão) e inglês.
- **Ícones:** lucide, empacotados offline via `@iconify/react/offline` a partir de um subconjunto
  versionado (`frontend/src/components/icons/lucide-subset.json`). O lucide tem licença ISC; a
  atribuição fica no bloco `info` do subconjunto. O app nunca chama uma API pública de ícones.
- **Livre de tema comercial:** o frontend não contém código nem assets do kit de template admin
  comercial sobre o qual foi construído no início. Todo componente, folha de estilo e arquivo de
  configuração foi escrito para este projeto.

### Verificador de licença

```bash
node scripts/check-theme-free.mjs [--json] [--similarity-threshold N]
```

Compara todos os arquivos em `frontend/` (ignorando `node_modules`, `dist` e `.vite`) com o kit
e reporta três coisas:

- **Arquivos idênticos:** mesmos bytes (sha256), qualquer que seja o nome ou o caminho.
- **Strings do fornecedor:** nomes do fornecedor e do produto do kit em qualquer lugar do frontend.
- **Quase-cópias:** depois de remover comentários, imports e linhas curtas, um arquivo falha
  quando pelo menos 40 % das suas linhas aparecem num único arquivo do kit (limite padrão `0.4`,
  ajustável com `--similarity-threshold`). Arquivos `.json` são comparados por sequências
  ordenadas de 5 linhas, porque suas linhas de chave/valor não têm como ser reescritas. Lockfiles
  ficam de fora.

Códigos de saída: `0` limpo, `1` algo encontrado, `2` kit ausente ou erro de uso.

O verificador precisa de uma cópia local do kit comprado em `themes/React/StarterKit/{TS,JS}`.
Essa pasta está no `.gitignore` e nunca é versionada. Sem ela o script sai com `2` de propósito,
para que um clone limpo nunca reporte um falso "passou". Quem não tem o kit não consegue rodá-lo;
os mantenedores rodam antes de cada release.

### Ícones

O subconjunto contém só os ícones que o código usa. Depois de adicionar ou renomear um ícone, rode:

```bash
node scripts/build-icon-subset.mjs           # regenera o subconjunto; depois faça commit
node scripts/build-icon-subset.mjs --check   # o gate: sai com 1 quando o subconjunto está desatualizado
```

### Testes end-to-end

Pré-requisitos: Docker e o Chromium do Playwright, instalado uma vez:

```bash
npx --prefix frontend playwright install chromium
```

Rodar a suíte inteira:

```bash
npm --prefix frontend run e2e
```

- Recria o projeto compose descartável `sfverify` com `down -v`, reconstrói as imagens a partir
  da sua working tree, cria o usuário pela tela de primeiro acesso e popula dados sintéticos. Os
  projetos rodam em ordem: `setup`, `app` e depois `security` (troca de senha e 2FA, por último
  porque alteram o único usuário).
- O Chromium roda com a interface em inglês e a página em pt-BR, de propósito: os campos de valor
  precisam aceitar `1234,56` mesmo num navegador que usa ponto decimal.
- O `sfverify` usa as mesmas portas da stack padrão (5433, 5080, 5173), então pare a sua stack
  antes. O script se recusa a rodar com ela no ar e nunca mexe nela nem nos dados dela.
- As credenciais são geradas a cada execução e nunca armazenadas. Relatórios e traces vão para a
  pasta `.e2e-out/` (no `.gitignore`) na raiz do repositório.
- Para derrubar a stack descartável: `npm --prefix frontend run e2e:stack -- down`.

### Gates

Rode a partir da raiz do repositório antes de um release:

```bash
npm --prefix frontend run build
npm --prefix frontend run lint        # 0 erros
npm --prefix frontend run typecheck
node --test scripts/*.test.mjs
node scripts/check-style-coverage.mjs
node scripts/check-style-contrast.mjs
node scripts/check-reachable.mjs
node scripts/build-icon-subset.mjs --check
node scripts/check-theme-free.mjs     # mantenedores, com a cópia local do kit
npm --prefix frontend run e2e
```

---

## Camada de IA

O SimpleFinance traz uma camada de IA opcional: categorização de transações assistida por LLM,
um assistente de chat, insights/anomalias/previsão determinísticos e um resumo mensal. **A camada
de IA é totalmente opcional** — o app é 100% funcional com a IA desligada, sem configuração ou
acima do orçamento (degradar com elegância é uma regra fixa, não um caminho de contingência).

### Variáveis de ambiente (só no servidor)

Só as duas **chaves** dos provedores e as **expressões cron** dos jobs são variáveis de ambiente.
Todo o resto que ajusta o comportamento da IA (teto de gasto, limiar de aplicação automática,
nomes de modelo por recurso, flags de ativação) fica em `UserSettings` (banco) e é editado na tela
**Configurações → IA** — não no ambiente.

| Variável | Para quê | Observações |
|----------|----------|-------------|
| `Ai__DeepSeekApiKey` | Chave bearer da DeepSeek (chat / completions / raciocínio) | Só no servidor. Nunca gravada no banco, nunca retornada pela API (o endpoint de configurações só informa se a chave *existe*). Sem a chave ⇒ degrada como numa queda (sem IA na nuvem). |
| `Ai__OpenAiApiKey` | Chave bearer da OpenAI (embeddings, `text-embedding-3-small`, 1536 dims) | Mesmo tratamento acima. |
| `Ai__EmbeddingCronExpression` | Backstop noturno de embeddings | Padrão `0 3 * * *` (03:00 no horário do servidor). |
| `Ai__CategorizationCronExpression` | Backstop noturno de categorização | Padrão `30 3 * * *` (depois dos embeddings). |
| `Ai__AnomalyCronExpression` | Detecção noturna de anomalias | Padrão `0 4 * * *`. |
| `Ai__InsightCronExpression` | Detecção diária de insights + redação | Padrão `30 4 * * *`. |
| `Ai__ForecastCronExpression` | Previsão diária do mês corrente | Padrão `0 5 * * *`. |
| `Ai__MonthlySummaryCronExpression` | Resumo mensal no dia 1 (a única chamada premium do mês) | Padrão `0 5 1 * *`. |

> O sublinhado duplo (`Ai__DeepSeekApiKey`) é o aninhamento de configuração do ASP.NET — ele
> mapeia para a chave `Ai:DeepSeekApiKey` (`AiOptions`).

A **tabela de preços** em USD por 1M de tokens (`Ai:Pricing`) é indexada pelo *nome* do modelo em
`appsettings.json`, então uma mudança de preço é edição de configuração, nunca um redeploy.

### Política de custo ("caro → barato")

A camada foi feita para ficar barata por padrão (teto padrão de **US$ 3,00 / mês**):

- **`deepseek-chat` é o modelo barato padrão** para categorização, assistente e redação de insights/previsão.
- **`deepseek-reasoner` (o modelo caro) é usado SÓ no resumo mensal do dia 1** — exatamente uma chamada premium por mês.
- **Embedding em lote noturno** — as transações são vetorizadas num job em lote fora do horário de pico (≤100 por execução), não a cada requisição.
- **Cache de embeddings** — reprocessar um texto idêntico é acerto de cache (por hash), então não custa nada.
- **Kill-switch do teto mensal** — `AiMonthlyBudgetUsd = 0` ⇒ **zero chamadas à nuvem**. Ao passar
  do teto, todas as chamadas à nuvem ficam bloqueadas até o fim do mês, **uma única** notificação
  é disparada, e o contador zera no dia 1. Acima do orçamento, cada recurso cai para o caminho
  determinístico (texto padrão, sem narrativa, resultados em cache) — nada quebra.

### Garantias de privacidade

- **O `PiiRedactor` remove dados pessoais antes de qualquer texto sair da VPS** para um LLM ou
  endpoint de embeddings: CNPJ (14), CPF (11), números de cartão (confirmados por Luhn),
  tokens/UUIDs/hex longos (IDs de item e connect tokens da Pluggy) e sequências soltas de 6+
  dígitos de conta/agência. Os padrões são aplicados numa ordem proposital (CNPJ → CPF → cartão →
  token → genérico) para os mais específicos ganharem.
- **As chaves de API ficam só no ambiente e nunca são expostas.** O endpoint de configurações só
  informa se cada chave existe — nunca o valor. As chaves nunca são gravadas no banco.
- **A IA é totalmente opcional.** Com a IA desligada ou acima do orçamento, o app continua 100%
  funcional — dashboard, orçamentos, relatórios e patrimônio funcionam com zero chamadas à nuvem.

### Ligar / desligar

Todas as flags ficam em `UserSettings` (banco), editadas em **Configurações → IA**:

- `AiEnabled` — chave geral. Com `false`, **nenhum** recurso de IA na nuvem roda, independente das flags por recurso, e a faixa de cards de IA do dashboard some.
- Flags por recurso: `AiCategorizationEnabled`, `AiAssistantEnabled`, `AiInsightsEnabled`, `AiAnomalyEnabled`, `AiForecastEnabled`, `AiMonthlySummaryEnabled`.

Todas começam em `false` — uma instalação nova roda com a IA desligada até você ativar.

### Descontinuação de modelos (só configuração)

A DeepSeek está descontinuando `deepseek-chat` / `deepseek-reasoner` em favor de
`deepseek-v4-flash` (~2026-07-24). Como os nomes dos modelos ficam em `UserSettings`
(`AiChatModel`, `AiSummaryModel`) e a tabela de preços é indexada por nome na configuração, migrar
é **só uma mudança de configuração em Configurações → IA mais uma linha de preço no `appsettings` —
sem mudar código e sem redeploy.**

### Limiares ajustáveis (mapa)

Todo padrão ajustável está centralizado numa classe nomeada, com um comentário convidando a
recalibrar com dados reais. Os padrões abaixo são razoáveis para um app de finanças pessoal
brasileiro de usuário único; a calibração com dados reais fica com você (ajuste no código e
recompile, ou via `UserSettings` onde indicado).

| Parâmetro | Padrão | Onde | Controla |
|-----------|--------|------|----------|
| `AiAutoApplyThreshold` | `0.85` | `UserSettings` (banco) + limites em `SimpleFinance.Application/Ai/AiSettingsValidator.cs` (`[0,1]`) | Confiança a partir da qual uma categorização da IA é aplicada automaticamente (abaixo disso vira sugestão). |
| `AiMonthlyBudgetUsd` | `3.00` | `UserSettings` (banco) | Teto mensal em USD; `0` desliga todas as chamadas à nuvem. |
| `AiChatModel` / `AiSummaryModel` | `deepseek-chat` / `deepseek-reasoner` | `UserSettings` (banco) | Nome do modelo por recurso (troca só por configuração). |
| Vizinhos kNN / piso de proximidade | `k = 10` / `sim ≥ 0.80` | `SimpleFinance.Infrastructure/Categorization/AiCategorizationService.cs` (`KnnNeighbors`, `ProximityFloorSim`); pontuação em `KnnCategoryVoter.cs` | Tamanho da votação kNN (nível 3 da categorização) + similaridade de cosseno mínima do vizinho mais próximo para confiar num voto. |
| Cortes do z-score modificado | Alerta `3.5` / Crítico `5.0`, histórico mínimo `6` | `SimpleFinance.Infrastructure/Ai/Insights/AnomalyThresholds.cs`; cálculo em `ZScoreStats.cs` | Cortes robustos de anomalia de Iglewicz–Hoaglin (mediana/MAD). |
| Pisos de anomalia | janela de duplicata `24h` / piso `R$20` / crítico `R$500`; aumento de assinatura `15%`/`50%`; salto de categoria `50%`/`R$200`/base `R$50`; histórico `12 meses` | `SimpleFinance.Infrastructure/Ai/Insights/AnomalyThresholds.cs` | Base contra falsos positivos na detecção de anomalias. |
| Pisos de qualificação de insights | variação de gasto `25%`/`R$50`/base `R$50`, alerta `50%`; risco de orçamento `80%`; tendência `10%`/`3 pts`; aumento de assinatura `15%`/`50%`; novo estabelecimento `R$100`; máx. `5` por execução | `SimpleFinance.Infrastructure/Ai/Insights/InsightThresholds.cs` | Pisos anti-ruído + limite de sinais por execução. |
| Mistura / faixa da previsão | mistura `0.5`, faixa `±1σ`, piso `10%`, histórico `3–6 meses`, base `3 meses`, top `5` fatores | `SimpleFinance.Infrastructure/Ai/Forecast/ForecastThresholds.cs` | Mistura da projeção determinística + largura da faixa de variação. |

### Solução de problemas

- **`ConcurrentSyncTests.SerializedNonOverlappingSyncLogWindows` falha localmente.** É um problema
  *conhecido e antigo* do ambiente de testes, sem relação com a IA e que não bloqueia nada: o teste
  precisa de um Postgres real com autenticação por senha (advisory locks não funcionam no EF
  InMemory), e um Postgres local configurado com SCRAM/sem senha recusa a conexão (erro SCRAM do
  Npgsql). Aponte `ConnectionStrings__Test` para um Postgres com autenticação por senha (veja
  `.env.example`) para rodá-lo.

## Licença

[MIT](LICENSE).
