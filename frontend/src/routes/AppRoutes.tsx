import { useRoutes } from 'react-router'

import { routes } from './index'

/** Renders the page matching the current URL from the app's route table. */
export function AppRoutes() {
  return useRoutes(routes)
}

export default AppRoutes
