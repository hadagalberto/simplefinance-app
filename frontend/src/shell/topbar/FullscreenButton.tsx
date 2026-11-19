import { Icon } from '@/components/Icon'
import { useSyncExternalStore } from 'react'
import { useTranslation } from 'react-i18next'

// The browser fires fullscreenchange on every enter and exit, including when
// the user leaves with Esc, so the button always reflects the real state.
function subscribe(onChange: () => void) {
  document.addEventListener('fullscreenchange', onChange)
  return () => document.removeEventListener('fullscreenchange', onChange)
}

function getSnapshot() {
  return document.fullscreenElement != null
}

function getServerSnapshot() {
  return false
}

/** Topbar button that puts the whole page in and out of browser fullscreen. */
export function FullscreenButton() {
  const { t } = useTranslation('shell')
  const isFullscreen = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot)

  // Some browsers (most mobile ones) do not allow fullscreen at all.
  if (typeof document === 'undefined' || !document.fullscreenEnabled) return null

  const label = isFullscreen ? t('fullscreen.exit') : t('fullscreen.enter')

  function handleClick() {
    // A refusal by the browser or the user is not an app error.
    if (document.fullscreenElement) {
      document.exitFullscreen().catch(() => {})
    } else {
      document.documentElement.requestFullscreen().catch(() => {})
    }
  }

  return (
    <span className="d-none d-sm-inline-flex">
      <button
        type="button"
        className="sf-topbar__btn"
        aria-label={label}
        title={label}
        aria-pressed={isFullscreen}
        onClick={handleClick}
      >
        <Icon icon={isFullscreen ? 'lucide:minimize' : 'lucide:maximize'} aria-hidden="true" />
      </button>
    </span>
  )
}
