# SimpleFinance

Self-hosted, single-user personal finance app. Connects to your Brazilian bank accounts,
cards and investments via **Pluggy (Open Finance Brasil)**, consolidates everything into one
view, and helps you understand spending, follow a budget, track net worth, detect recurring
subscriptions, hit goals and receive alerts. The source of truth is what the banks return via
Pluggy — there is no manual transaction entry.

Full canonical specification: [`docs/PRD-SimpleFinance.md`](docs/PRD-SimpleFinance.md) (Portuguese). The AI layer specification lives in
[`docs/PRD-SimpleFinance-IA.md`](docs/PRD-SimpleFinance-IA.md).

## Stack

- **Backend:** .NET 10 (ASP.NET Core), EF Core (domain/CRUD) + Dapper (reports), Hangfire (jobs), PostgreSQL 17 + pgvector.
- **Frontend:** React 19 + Vite + TypeScript, TanStack Query, ApexCharts, react-router 7, Bootstrap 5 (see [Frontend](#frontend)).
- **Deploy:** Docker Compose on a VPS, Caddy reverse proxy (automatic HTTPS), migrations applied automatically on API start.

## Getting started

1. `cp .env.example .env` and fill in real values (see the comments in `.env.example`).
2. `docker compose up -d` — the API applies EF migrations on start.
3. The frontend is served at the origin configured by `Cors__AllowedOrigin` / `VITE_API_BASE_URL`.

Never commit `.env` — it is gitignored. All secrets (Pluggy client/secret, JWT signing key,
Telegram/SMTP credentials, AI provider keys) live in environment variables only, never in the DB.

## Frontend

The web client lives in `frontend/`.

- **Stack:** React 19 + Vite + TypeScript; Bootstrap 5 and react-bootstrap from npm; an authored
  SCSS theme in `frontend/src/styles/` (dark by default, light available from the topbar);
  ApexCharts; TanStack Query; react-hook-form + zod; i18n in pt-BR (default) and English.
- **Icons:** lucide, bundled offline through `@iconify/react/offline` from a committed subset
  (`frontend/src/components/icons/lucide-subset.json`). lucide is ISC-licensed; its attribution
  stays in the subset's `info` block. The app never requests a public icon API.
- **Theme-free:** the frontend contains no code or assets from the commercial admin-template kit
  it was first built on. Every component, stylesheet and config file is authored for this project.

### License verifier

```bash
node scripts/check-theme-free.mjs [--json] [--similarity-threshold N]
```

It compares every file under `frontend/` (`node_modules`, `dist` and `.vite` are skipped)
with the kit and reports three things:

- **Identical files:** same bytes (sha256), whatever the name or path.
- **Vendor strings:** the kit's vendor and product names anywhere in the frontend.
- **Near-duplicates:** after comments, imports and short lines are removed, a file fails when
  at least 40 % of its lines are found in one single kit file (default threshold `0.4`,
  `--similarity-threshold` changes it). `.json` files are compared by ordered 5-line runs
  instead, because their key/value lines cannot be reworded. Lockfiles are excluded.

Exit codes: `0` clean, `1` a finding, `2` the kit is missing or a usage error.

The verifier needs a local copy of the purchased kit under `themes/React/StarterKit/{TS,JS}`.
That folder is gitignored and is never committed. Without it the script exits `2` by design,
so a clean clone can never report a false pass. Contributors without the kit cannot run it;
maintainers run it before each release.

### Icons

The subset holds only the icons the source uses. After you add or rename an icon name, run:

```bash
node scripts/build-icon-subset.mjs           # regenerate the subset, then commit it
node scripts/build-icon-subset.mjs --check   # the gate: exit 1 when the subset is stale
```

### End-to-end tests

Prerequisites: Docker, and the Chromium browser for Playwright, installed once:

```bash
npx --prefix frontend playwright install chromium
```

Run the whole suite:

```bash
npm --prefix frontend run e2e
```

- It resets the throwaway compose project `sfverify` with `down -v`, rebuilds the images from
  your working tree, creates the user through the first-run setup page and seeds synthetic data.
  The projects run in order: `setup`, `app`, then `security` (password change and 2FA, last
  because they change the only user).
- `sfverify` uses the same ports as the default stack (5433, 5080, 5173), so stop your own
  stack first. The script refuses to run while it is up and never touches it or its data.
- Credentials are generated for each run and never stored. Reports and traces go to the
  gitignored `.e2e-out/` folder at the repo root.
- Stop the throwaway stack with `npm --prefix frontend run e2e:stack -- down`.

### Gates

Run these from the repo root before a release:

```bash
npm --prefix frontend run build
npm --prefix frontend run lint        # 0 errors
npm --prefix frontend run typecheck
node --test scripts/*.test.mjs
node scripts/check-style-coverage.mjs
node scripts/check-style-contrast.mjs
node scripts/check-reachable.mjs
node scripts/build-icon-subset.mjs --check
node scripts/check-theme-free.mjs     # maintainers, with the local kit copy
npm --prefix frontend run e2e
```

---

## AI layer (v3.0)

SimpleFinance ships an optional AI layer (Phases 15–20 of the IA milestone): LLM-assisted
transaction categorization, an AI chat assistant, deterministic insights/anomalies/forecast, and
a monthly summary. **The AI layer is entirely optional** — the app is 100% functional with AI
turned off, unconfigured, or over budget (graceful degradation is a hard invariant, not a
fallback path).

### Environment variables (server-side only)

Only the two provider **keys** and the job **cron expressions** are env vars. Everything else that
tunes AI behaviour (budget cap, auto-apply threshold, per-feature model names, enable flags) lives
in `UserSettings` (DB) and is edited from the **Settings → IA** screen — not the environment.

| Variable | Purpose | Notes |
|----------|---------|-------|
| `Ai__DeepSeekApiKey` | DeepSeek bearer key (chat / completions / reasoning) | Server-side only. Never stored in the DB, never returned by the API (the settings endpoint reports only key-*present* status). Missing key ⇒ degrades like an outage (no cloud AI). |
| `Ai__OpenAiApiKey` | OpenAI bearer key (embeddings, `text-embedding-3-small`, 1536 dims) | Same handling as above. |
| `Ai__EmbeddingCronExpression` | Nightly embedding backstop | Default `0 3 * * *` (03:00 server-local). |
| `Ai__CategorizationCronExpression` | Nightly categorization backstop | Default `30 3 * * *` (after embeddings). |
| `Ai__AnomalyCronExpression` | Nightly anomaly detection | Default `0 4 * * *`. |
| `Ai__InsightCronExpression` | Daily insight detection + phrasing | Default `30 4 * * *`. |
| `Ai__ForecastCronExpression` | Daily current-month forecast | Default `0 5 * * *`. |
| `Ai__MonthlySummaryCronExpression` | Day-1 monthly summary (the one premium call/month) | Default `0 5 1 * *`. |

> The double-underscore (`Ai__DeepSeekApiKey`) is ASP.NET config nesting — it binds to the
> `Ai:DeepSeekApiKey` configuration key (`AiOptions`).

The per-model USD-per-1M-token **pricing table** (`Ai:Pricing`) is keyed by model *name* in
`appsettings.json`, so a rate change is a config edit, never a redeploy.

### Cost policy ("caro → barato")

The layer is engineered to stay cheap by default (default cap **US$ 3.00 / month**):

- **`deepseek-chat` is the default cheap model** for categorization, the assistant, insight/forecast phrasing.
- **`deepseek-reasoner` (the expensive model) is used ONLY for the day-1 monthly summary** — exactly one premium call per month.
- **Nightly batch embedding** — transactions are vectorized in an off-peak batched job (≤100/run), not inline per request.
- **Embedding cache** — an identical text re-embed is a cache hit (hash-keyed), so it costs nothing.
- **Hard monthly cap kill-switch** — `AiMonthlyBudgetUsd = 0` ⇒ **zero cloud calls**. Crossing the
  cap blocks all further cloud calls for the month, fires **exactly one** notification, and resets
  on the 1st. When over budget every feature falls back to its deterministic path (fallback prose,
  no narrative, cached results) — nothing breaks.

### Privacy guarantees

- **`PiiRedactor` strips PII before any text leaves the VPS** to an LLM or embedding endpoint:
  CNPJ (14), CPF (11), card-like numbers (Luhn-confirmed), token/UUID/long-hex secrets (Pluggy
  item IDs & connect tokens), and bare 6+ digit account/agency runs. Patterns are applied in a
  deliberate order (CNPJ → CPF → card → token → generic) so the specific passes win.
- **API keys are env-only and never surfaced.** The settings endpoint reports only whether each
  key is present — never the value. Keys are never written to the DB.
- **AI is fully optional.** With AI off or over budget the app stays 100% functional — the
  dashboard, budgets, reports and net-worth tracking all work with zero cloud calls.

### Enable / disable

All flags live in `UserSettings` (DB), edited from **Settings → IA**:

- `AiEnabled` — master switch. When `false`, **no** cloud AI feature runs regardless of per-feature flags, and the dashboard AI card strip is hidden.
- Per-feature toggles: `AiCategorizationEnabled`, `AiAssistantEnabled`, `AiInsightsEnabled`, `AiAnomalyEnabled`, `AiForecastEnabled`, `AiMonthlySummaryEnabled`.

All default to `false` — a fresh install runs with AI off until you opt in.

### Model-name deprecation (config-only)

DeepSeek is deprecating `deepseek-chat` / `deepseek-reasoner` in favour of `deepseek-v4-flash`
(~2026-07-24). Because the model names live in `UserSettings` (`AiChatModel`, `AiSummaryModel`) and
the pricing table is keyed by name in config, migrating is a **config-only change via Settings → IA
plus an `appsettings` pricing row — no code change and no redeploy.**

### Tunable thresholds (map)

Every tunable default is centralized in a named holder class with a comment inviting real-data
re-tuning. Defaults below are confirmed sensible for a single-user BR finance app; real-data
tuning is left to you (adjust in code and rebuild, or via `UserSettings` where noted).

| Knob | Default | Location | Controls |
|------|---------|----------|----------|
| `AiAutoApplyThreshold` | `0.85` | `UserSettings` (DB) + bounds in `SimpleFinance.Application/Ai/AiSettingsValidator.cs` (`[0,1]`) | Confidence at/above which an AI categorization auto-applies (else a suggestion). |
| `AiMonthlyBudgetUsd` | `3.00` | `UserSettings` (DB) | Hard monthly USD cap; `0` disables all cloud calls. |
| `AiChatModel` / `AiSummaryModel` | `deepseek-chat` / `deepseek-reasoner` | `UserSettings` (DB) | Per-feature model names (config-only deprecation path). |
| kNN neighbours / proximity floor | `k = 10` / `sim ≥ 0.80` | `SimpleFinance.Infrastructure/Categorization/AiCategorizationService.cs` (`KnnNeighbors`, `ProximityFloorSim`); scoring in `KnnCategoryVoter.cs` | kNN categorization level-3 vote size + minimum nearest-neighbour cosine similarity to trust a vote. |
| Modified z-score cutoffs | Warn `3.5` / Critical `5.0`, min history `6` | `SimpleFinance.Infrastructure/Ai/Insights/AnomalyThresholds.cs`; math in `ZScoreStats.cs` | Iglewicz–Hoaglin robust anomaly cutoffs (median/MAD). |
| Anomaly floors | duplicate window `24h` / floor `R$20` / critical `R$500`; sub-increase `15%`/`50%`; category jump `50%`/`R$200`/baseline `R$50`; lookback `12mo` | `SimpleFinance.Infrastructure/Ai/Insights/AnomalyThresholds.cs` | False-positive spine for anomaly detection. |
| Insight qualification floors | spending-change `25%`/`R$50`/baseline `R$50`, warn `50%`; budget-risk `80%`; trend `10%`/`3 pts`; sub-creep `15%`/`50%`; new-merchant `R$100`; max `5`/run | `SimpleFinance.Infrastructure/Ai/Insights/InsightThresholds.cs` | Anti-noise floors + per-run signal cap for insights. |
| Forecast blend / range | blend `0.5`, range `±1σ`, floor `10%`, history `3–6mo`, baseline `3mo`, top `5` drivers | `SimpleFinance.Infrastructure/Ai/Forecast/ForecastThresholds.cs` | Deterministic projection blend + variance band width. |

### Troubleshooting

- **`ConcurrentSyncTests.SerializedNonOverlappingSyncLogWindows` fails locally.** This is a
  *known, pre-existing, non-AI, non-blocking* test-environment issue: the test needs a real
  Postgres with password auth (advisory locks aren't supported by EF InMemory), and a local
  Postgres configured for SCRAM/no-password rejects the connection (Npgsql SCRAM error). Point
  `ConnectionStrings__Test` at a password-auth Postgres (see `.env.example`) to run it. It is not
  an AI regression and does not block the AI milestone.

## License

[MIT](LICENSE).
