import { PluggyConnect } from 'pluggy-connect-sdk'

import { useNotificationContext } from '@/shell/toast/useNotificationContext'

import { useCreateConnectTokenMutation, useReconnectTokenMutation } from './api'

/**
 * Thin wrapper hook around pluggy-connect-sdk's real widget class
 * (`new PluggyConnect(props).init()`, verified against the installed
 * 2.13.0 package's own .d.ts).
 *
 * Mints a fresh connect-token immediately before every widget open (PLG-02's
 * "never cached/reused" rule) and never persists the token client-side.
 * Reconnect (update mode) is achieved by minting the token via the
 * per-connection reconnect-token endpoint instead of the generic
 * connect-token endpoint -- Pluggy scopes that token to the existing item
 * server-side, so update-mode behavior is enforced by which token endpoint
 * is called, not by a client-supplied itemId (the GET /api/connections
 * contract deliberately never exposes the raw Pluggy itemId to the
 * frontend).
 */

const WIDGET_ERROR_COPY = "Couldn't open the connection window. Please try again."

export interface OpenConnectWidgetOptions {
  accessToken: string
  /** Our internal Connection.Id -- when set, opens the widget in update/reconnect mode. */
  reconnectConnectionId?: string
  onSuccess: (pluggyItemId: string) => void
}

export function usePluggyConnect() {
  const createConnectTokenMutation = useCreateConnectTokenMutation()
  const reconnectTokenMutation = useReconnectTokenMutation()
  const { showNotification } = useNotificationContext()

  async function openConnectWidget({
    accessToken,
    reconnectConnectionId,
    onSuccess,
  }: OpenConnectWidgetOptions): Promise<void> {
    try {
      const { connectToken } = reconnectConnectionId
        ? await reconnectTokenMutation.mutateAsync({ accessToken, id: reconnectConnectionId })
        : await createConnectTokenMutation.mutateAsync({ accessToken })

      const widget = new PluggyConnect({
        connectToken,
        includeSandbox: true,
        onSuccess: (data) => onSuccess(data.item.id),
        onError: () => {
          showNotification({ message: WIDGET_ERROR_COPY, variant: 'danger' })
        },
      })

      await widget.init()
    } catch {
      showNotification({ message: WIDGET_ERROR_COPY, variant: 'danger' })
    }
  }

  return {
    openConnectWidget,
    isOpening: createConnectTokenMutation.isPending || reconnectTokenMutation.isPending,
  }
}
