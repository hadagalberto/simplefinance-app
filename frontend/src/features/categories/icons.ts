/**
 * Curated ~30-icon set for the Categories icon picker (mirrors v1's
 * CATEGORY_ICONS record keys from
 * frontend/src/features/settings/categories/icons.ts) -- NOT the entire
 * lucide set, to keep the searchable picker list small and predictable.
 * Includes every icon Phase-4's CategorySeeder actually seeded (Utensils,
 * Car, Home, HeartPulse, PartyPopper, GraduationCap, ShoppingBag, Wrench,
 * Wallet, TrendingUp, ArrowLeftRight) plus a general-purpose set for
 * user-created categories. `Tag` doubles as both a real pickable icon and
 * the fallback rendered when a category has no `icon` value.
 *
 * These PascalCase strings MUST stay the exact wire/stored values
 * (unchanged) since they match what the Phase-4 seed already wrote into
 * every existing user's Category.icon column.
 */
export const CATEGORY_ICON_NAMES: string[] = [
  'Utensils',
  'Car',
  'Home',
  'HeartPulse',
  'PartyPopper',
  'GraduationCap',
  'ShoppingBag',
  'Wrench',
  'Wallet',
  'TrendingUp',
  'ArrowLeftRight',
  'ShoppingCart',
  'UtensilsCrossed',
  'Bike',
  'Fuel',
  'Bus',
  'Building2',
  'Landmark',
  'Pill',
  'Plane',
  'Tv',
  'Shirt',
  'Laptop',
  'CreditCard',
  'PiggyBank',
  'Banknote',
  'Receipt',
  'Gift',
  'Dumbbell',
  'Coffee',
  'Tag',
]

/**
 * Icons the backend's first-run category seed stores on subcategories that are
 * not offered in the picker above. They are listed so the bundled icon subset
 * (scripts/build-icon-subset.mjs treats every `*ICON_NAMES` entry as required)
 * always holds them; scripts/build-icon-subset.test.mjs checks the seed source
 * against the committed subset.
 */
export const SEEDED_CATEGORY_ICON_NAMES: string[] = [
  'Briefcase',
  'Building',
  'CarTaxiFront',
  'Plug',
  'ShieldPlus',
  'Smartphone',
]

const FALLBACK_ICON_NAME = 'Tag'

/**
 * Generic PascalCase -> kebab-case converter, matching the iconify `lucide`
 * collection's slug naming (which follows lucide's own icon filenames):
 * `HeartPulse` -> `heart-pulse`, `ArrowLeftRight` -> `arrow-left-right`,
 * `UtensilsCrossed` -> `utensils-crossed`, `PiggyBank` -> `piggy-bank`.
 * A letter followed by a digit is split too, as lucide does:
 * `Building2` -> `building-2`. scripts/build-icon-subset.mjs uses the same rule.
 */
export function toIconifySlug(name: string): string {
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/([A-Za-z])([0-9])/g, '$1-$2')
    .toLowerCase()
}

/**
 * Resolves a category's stored PascalCase icon name (e.g. `Category.icon`
 * from the API) to the full `lucide:{slug}` iconify name, which the app's
 * `Icon` component (src/components/Icon.tsx) accepts as its `icon` prop.
 * Falls back to `Tag` when `name` is absent, matching v1's
 * resolveIcon-fallback-to-Tag behavior.
 */
export function resolveCategoryIcon(name?: string | null): string {
  return `lucide:${toIconifySlug(name ?? FALLBACK_ICON_NAME)}`
}
