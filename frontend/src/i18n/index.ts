import i18n, { type Resource } from 'i18next'
import { initReactI18next } from 'react-i18next'
import LanguageDetector from 'i18next-browser-languagedetector'

/**
 * i18n namespace convention (auto-loaded)
 * --------------------------------------------------------------------------
 * Every feature area gets its own translation namespace so per-feature work
 * can add strings WITHOUT touching this shared setup file.
 *
 * To add a namespace for a new feature (e.g. "transactions"):
 *   1. Create `src/i18n/locales/pt-BR/transactions.json` and
 *      `src/i18n/locales/en/transactions.json` (same key shape in both).
 *   2. That's it — the glob below auto-registers it. In components call
 *      `useTranslation('transactions')` (add 'common' for shared strings:
 *      `useTranslation(['transactions', 'common'])`).
 *
 * `common` is the default namespace (app-wide actions / loading / app name);
 * `menu` holds the navigation labels. Resources are bundled at build time
 * (no HTTP backend), so react-i18next renders synchronously — no Suspense.
 */

// Vite eagerly bundles every locale JSON. Path shape: ./locales/<lng>/<ns>.json
const localeModules = import.meta.glob('./locales/*/*.json', { eager: true }) as Record<
  string,
  { default: Record<string, unknown> }
>

const resources: Resource = {}
const namespaceSet = new Set<string>()
for (const path in localeModules) {
  const match = path.match(/\.\/locales\/([^/]+)\/([^/]+)\.json$/)
  if (!match) continue
  const [, lng, ns] = match
  resources[lng] ??= {}
  resources[lng][ns] = localeModules[path].default
  namespaceSet.add(ns)
}
const namespaces = Array.from(namespaceSet)

export const SUPPORTED_LANGUAGES = ['pt-BR', 'en'] as const
export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number]

export const LANGUAGE_STORAGE_KEY = 'lang'
const DEFAULT_LANGUAGE: SupportedLanguage = 'pt-BR'

const isSupportedLanguage = (value: string | null): value is SupportedLanguage => !!value && (SUPPORTED_LANGUAGES as readonly string[]).includes(value)

const savedLanguage = typeof window !== 'undefined' ? window.localStorage.getItem(LANGUAGE_STORAGE_KEY) : null

// Resolution order: saved choice (localStorage 'lang') -> default 'pt-BR'.
// We resolve the initial language ourselves (rather than letting the
// detector's navigator/htmlTag heuristics pick it) so a first-time visitor
// always lands on pt-BR. LanguageDetector stays wired up so it can cache
// the language to localStorage whenever i18n.changeLanguage() is called
// (see the language switcher in src/shell/topbar/LanguageMenu.tsx).
const initialLanguage: SupportedLanguage = isSupportedLanguage(savedLanguage) ? savedLanguage : DEFAULT_LANGUAGE

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources,
    lng: initialLanguage,
    fallbackLng: 'en',
    supportedLngs: SUPPORTED_LANGUAGES,
    ns: namespaces,
    defaultNS: 'common',
    interpolation: {
      escapeValue: false, // React already escapes values
    },
    detection: {
      order: ['localStorage'],
      lookupLocalStorage: LANGUAGE_STORAGE_KEY,
      caches: ['localStorage'],
    },
    react: {
      useSuspense: false,
    },
  })

export default i18n
