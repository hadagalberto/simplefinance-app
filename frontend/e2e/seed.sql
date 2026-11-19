-- Synthetic data for the e2e suite. Runs once per run, right after first-run
-- setup created the only user, on a throwaway database that `stack.mjs reset`
-- has just recreated, so it needs no idempotence.
--
-- Every date is relative to now(), so the dashboard has a current month and
-- five months of history on any day. Category ids are looked up by the names
-- the API seeds for each new user. Nothing here resembles real bank data.
SET client_encoding = 'UTF8';

BEGIN;

CREATE TEMP TABLE e2e_ids ON COMMIT DROP AS
SELECT
  (SELECT "Id" FROM "Users" LIMIT 1) AS user_id,
  gen_random_uuid() AS connection_id,
  gen_random_uuid() AS checking_id,
  gen_random_uuid() AS card_id,
  date_trunc('month', now()) AS month_start;

DO $$
BEGIN
  IF (SELECT user_id FROM e2e_ids) IS NULL THEN
    RAISE EXCEPTION 'e2e seed: no user yet, run first-run setup before seeding';
  END IF;
END
$$;

-- One healthy connection (enum ConnectionStatus: 1 = Updated).
INSERT INTO "Connections" ("Id", "UserId", "PluggyItemId", "ConnectorId", "ConnectorName", "InstitutionName",
                           "Status", "ExecutionStatus", "LastSyncAt", "CreatedAt", "UpdatedAt")
SELECT connection_id, user_id, 'e2e-item-1', 9001, 'E2E Banco', 'E2E Banco',
       1, 'SUCCESS', now() - interval '2 hours', now() - interval '6 months', now() - interval '2 hours'
FROM e2e_ids;

-- A checking account (AccountType 0 = Bank, AccountSubtype 0 = CheckingAccount)
-- and a credit card (AccountType 1 = Credit, AccountSubtype 2 = CreditCard).
INSERT INTO "Accounts" ("Id", "UserId", "ConnectionId", "PluggyAccountId", "Type", "Subtype", "Name",
                        "MarketingName", "NumberMasked", "Balance", "CurrencyCode", "CreatedAt", "UpdatedAt")
SELECT checking_id, user_id, connection_id, 'e2e-acc-checking', 0, 0, 'E2E Conta Corrente',
       'E2E Conta Corrente', '0001-9', 8450.32, 'BRL', now() - interval '6 months', now()
FROM e2e_ids
UNION ALL
SELECT card_id, user_id, connection_id, 'e2e-acc-card', 1, 2, 'E2E Cartão',
       'E2E Cartão Platinum', '**** 4242', 1830.55, 'BRL', now() - interval '6 months', now()
FROM e2e_ids;

-- Card details (CreditCardStatus 0 = Active); due in 5 days, so "upcoming due dates" has an entry.
INSERT INTO "CreditCardDetails" ("Id", "AccountId", "Brand", "Level", "CreditLimit", "AvailableCredit",
                                 "LastStatementBalance", "MinimumPayment", "BalanceCloseDate", "BalanceDueDate",
                                 "Status", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), card_id, 'VISA', 'PLATINUM', 8000.00, 6169.45,
       1830.55, 183.06, now() - interval '2 days', now() + interval '5 days',
       0, now() - interval '6 months', now()
FROM e2e_ids;

-- Transactions over the current month and the five before it, never in the future.
-- TransactionType 0 = Debit, 1 = Credit; TransactionStatus 1 = Posted; CategorySource 0 = SystemMapping.
-- Monthly salary on the checking account.
INSERT INTO "Transactions" ("Id", "UserId", "AccountId", "PluggyTransactionId", "Date", "Description", "Amount",
                            "Type", "CurrencyCode", "Status", "CategoryId", "CategorySource", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), ids.user_id, ids.checking_id, format('e2e-tx-salary-%s', m),
       LEAST(ids.month_start - make_interval(months => m) + interval '4 days 9 hours', now() - interval '1 minute'),
       format('E2E Salário %s', lpad((m + 1)::text, 2, '0')), 9500.00,
       1, 'BRL', 1, (SELECT "Id" FROM "Categories" WHERE "UserId" = ids.user_id AND "Name" = 'Salário' LIMIT 1),
       0, now(), now()
FROM e2e_ids ids
CROSS JOIN generate_series(0, 5) AS m;

-- Twenty expenses per month across seven categories, alternating checking and card.
INSERT INTO "Transactions" ("Id", "UserId", "AccountId", "PluggyTransactionId", "Date", "Description", "Amount",
                            "Type", "CurrencyCode", "Status", "CategoryId", "CategorySource", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), ids.user_id,
       CASE WHEN i % 3 = 0 THEN ids.card_id ELSE ids.checking_id END,
       format('e2e-tx-%s-%s', m, i),
       LEAST(ids.month_start - make_interval(months => m) + make_interval(days => (i * 7) % 27, hours => 8 + i % 12),
             now() - interval '1 minute'),
       format('E2E %s %s', cat.label, lpad((m * 20 + i)::text, 3, '0')),
       -round((25 + (i * 37) % 180 + m * 3.5)::numeric, 2),
       0, 'BRL', 1,
       (SELECT "Id" FROM "Categories" WHERE "UserId" = ids.user_id AND "Name" = cat.name LIMIT 1),
       0, now(), now()
FROM e2e_ids ids
CROSS JOIN generate_series(0, 5) AS m
CROSS JOIN generate_series(1, 20) AS i
CROSS JOIN LATERAL (
  SELECT (ARRAY['Mercado', 'Restaurante e Bar', 'Combustível', 'Farmácia', 'Streaming', 'Vestuário', 'Delivery'])[1 + i % 7] AS name,
         (ARRAY['Mercado', 'Restaurante', 'Posto', 'Farmácia', 'Streaming', 'Loja', 'Delivery'])[1 + i % 7] AS label
) AS cat;

-- Budgets for the current month (the API reads Year/Month in UTC). Mercado is set
-- so that this month's spend is about 90 % of the limit, which puts it at risk.
INSERT INTO "Budgets" ("Id", "UserId", "CategoryId", "Year", "Month", "LimitAmount", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), ids.user_id, c."Id",
       EXTRACT(YEAR FROM now() AT TIME ZONE 'UTC')::int, EXTRACT(MONTH FROM now() AT TIME ZONE 'UTC')::int,
       CASE c."Name"
         WHEN 'Mercado' THEN GREATEST(round(COALESCE((
           SELECT sum(abs(t."Amount")) FROM "Transactions" t
           WHERE t."UserId" = ids.user_id AND t."CategoryId" = c."Id" AND t."Date" >= ids.month_start
         ), 0) / 0.9, 2), 1.00)
         WHEN 'Restaurante e Bar' THEN 5000.00
         ELSE 3000.00
       END,
       now(), now()
FROM e2e_ids ids
JOIN "Categories" c ON c."UserId" = ids.user_id AND c."Name" IN ('Mercado', 'Restaurante e Bar', 'Combustível');

-- Investments of three types (InvestmentType 1 = FixedIncome, 0 = MutualFund, 2 = Equity).
INSERT INTO "Investments" ("Id", "UserId", "ConnectionId", "PluggyInvestmentId", "Name", "Type", "Subtype",
                           "CurrencyCode", "Balance", "Value", "AmountInvested", "Quantity", "Institution",
                           "Date", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), ids.user_id, ids.connection_id, inv.pluggy_id, inv.name, inv.type, inv.subtype,
       'BRL', inv.value, inv.value, inv.invested, inv.quantity, 'E2E Banco',
       now() - interval '1 day', now() - interval '6 months', now()
FROM e2e_ids ids
CROSS JOIN (VALUES
  ('e2e-inv-cdb', 'E2E CDB Liquidez Diária', 1, 'CDB', 12500.00, 12000.00, 1.0),
  ('e2e-inv-fund', 'E2E Fundo Multimercado', 0, 'MULTIMARKET_FUND', 7340.18, 7000.00, 312.4),
  ('e2e-inv-stock', 'E2E Ações BR', 2, 'STOCK', 4210.90, 4500.00, 150.0)
) AS inv(pluggy_id, name, type, subtype, value, invested, quantity);

-- Six monthly net-worth snapshots, oldest first, the newest one an hour ago.
INSERT INTO "NetWorthSnapshots" ("Id", "UserId", "Date", "AccountsBalance", "InvestmentsBalance", "CreditCardDebt",
                                 "TotalAssets", "TotalLiabilities", "NetWorth", "CreatedAt", "UpdatedAt")
SELECT gen_random_uuid(), ids.user_id,
       CASE WHEN m = 0 THEN now() - interval '1 hour'
            ELSE ids.month_start - make_interval(months => m) + interval '27 days' END,
       s.accounts, s.investments, s.debt,
       s.accounts + s.investments, s.debt, s.accounts + s.investments - s.debt,
       now(), now()
FROM e2e_ids ids
CROSS JOIN generate_series(0, 5) AS m
CROSS JOIN LATERAL (
  SELECT (6200 + (5 - m) * 450)::numeric(18, 2) AS accounts,
         (19000 + (5 - m) * 1100)::numeric(18, 2) AS investments,
         (1500 + m * 120)::numeric(18, 2) AS debt
) AS s;

COMMIT;
