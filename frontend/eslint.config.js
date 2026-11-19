// Lint setup for the SimpleFinance web client (TypeScript + React, flat config).
//
// The file is organised as data first: which paths exist, which presets the
// browser code builds on, and which rule severities this project overrides.
// The exported config at the bottom only assembles those pieces.
import js from '@eslint/js'
import { defineConfig, globalIgnores } from 'eslint/config'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import globals from 'globals'
import tseslint from 'typescript-eslint'

const ERROR = 'error'
const WARN = 'warn'
const OFF = 'off'

/** Path groups. The test runner and its config execute in Node, not in the browser. */
const paths = {
  generatedOutput: ['dist'],
  typescriptSources: ['**/*.{ts,tsx}'],
  nodeTooling: ['e2e/**/*.ts', 'playwright.config.ts'],
}

/** Same severity for a list of rule ids. */
const allAt = (severity, ruleIds) => Object.fromEntries(ruleIds.map((id) => [id, severity]))
const hooksRuleIds = (names) => names.map((name) => `react-hooks/${name}`)

// Upstream presets, in the order they apply: core JavaScript, typescript-eslint,
// the React hooks plugin, then the fast-refresh check tuned for a Vite dev server.
const browserPresets = [
  js.configs.recommended,
  tseslint.configs.recommended,
  reactHooks.configs.flat.recommended,
  reactRefresh.configs.vite,
]

// Several hooks rules target the React Compiler, which this build does not use.
// Their reports are refactoring hints, so they are shown as warnings and never
// fail the lint run.
const compilerHintRules = hooksRuleIds(['incompatible-library', 'set-state-in-effect', 'purity', 'immutability', 'refs'])

// The package root of the icon library is its online build, which downloads
// icon data from a public API at runtime. Icons go through the app's component,
// which renders from the bundled subset; the `/offline` subpath stays allowed.
const restrictedImports = {
  paths: [
    {
      name: '@iconify/react',
      message: "Use the app's Icon from '@/components/Icon' (bundled icons, no network request).",
    },
  ],
}

const browserRuleOverrides = {
  'no-restricted-imports': [ERROR, restrictedImports],
  ...allAt(WARN, compilerHintRules),
  // A module that mixes components with other exports only costs a full page
  // reload during development instead of a hot swap; behaviour is unaffected.
  'react-refresh/only-export-components': WARN,
  // Some API adapters still accept loosely typed payloads. New `any` usages are
  // flagged, the existing ones do not block a build.
  '@typescript-eslint/no-explicit-any': WARN,
}

const browserCode = {
  files: paths.typescriptSources,
  extends: browserPresets,
  languageOptions: { ecmaVersion: 2022, globals: globals.browser },
  rules: browserRuleOverrides,
}

// End-to-end tests have no components, and the Playwright fixture callback is
// called `use`, which the hooks plugin would read as a React hook. Every hooks
// rule and the fast-refresh check are therefore disabled for Node tooling.
const nodeTooling = {
  files: paths.nodeTooling,
  languageOptions: { globals: { ...globals.node } },
  rules: {
    ...allAt(OFF, hooksRuleIds(Object.keys(reactHooks.rules))),
    'react-refresh/only-export-components': OFF,
  },
}

const config = defineConfig([globalIgnores(paths.generatedOutput), browserCode, nodeTooling])

export default config
