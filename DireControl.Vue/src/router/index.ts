import { createRouter, createWebHistory } from 'vue-router'
import MapView from '@/views/MapView.vue'
import { DEFAULT_SETTINGS_SECTION, isSettingsSection } from '@/views/settings/sections'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'map',
      component: MapView,
    },
    {
      path: '/radio',
      name: 'radio',
      component: () => import('@/views/RadioView.vue'),
    },
    {
      path: '/stream',
      name: 'stream',
      component: () => import('@/views/BeaconStreamView.vue'),
    },
    // Old bookmark: the beacon stream became Stream.
    { path: '/beacons', redirect: (to) => ({ path: '/stream', query: to.query }) },
    {
      path: '/messages',
      name: 'messages',
      component: () => import('@/views/MessagesView.vue'),
    },
    {
      path: '/terminal',
      name: 'terminal',
      component: () => import('@/views/TerminalView.vue'),
    },
    // Developer-only symbol sheet: not routed in production builds.
    ...(import.meta.env.DEV
      ? [
          {
            path: '/aprs-icon-test',
            name: 'aprs-icon-test',
            component: () => import('@/views/AprsIconTest.vue'),
          },
        ]
      : []),
    {
      path: '/alerts',
      name: 'alerts',
      component: () => import('@/views/AlertsView.vue'),
    },
    // Old ?tab= bookmarks and bare /settings land on a section route.
    {
      path: '/settings',
      redirect: (to) => {
        const { tab, ...query } = to.query
        return {
          path: `/settings/${isSettingsSection(tab) ? tab : DEFAULT_SETTINGS_SECTION}`,
          query,
        }
      },
    },
    {
      path: '/settings/:section',
      name: 'settings',
      component: () => import('@/views/SettingsView.vue'),
      beforeEnter: (to) =>
        isSettingsSection(to.params.section)
          ? true
          : { path: `/settings/${DEFAULT_SETTINGS_SECTION}`, query: to.query },
    },
    {
      path: '/setup',
      name: 'setup',
      component: () => import('@/views/SetupView.vue'),
    },
    {
      path: '/radio/diagnostics',
      name: 'diagnostics',
      component: () => import('@/views/DiagnosticsView.vue'),
    },
    // Statistics and Frequencies merged into Network tabs — keep old bookmarks.
    { path: '/frequencies', redirect: { path: '/network', query: { tab: 'frequencies' } } },
    {
      path: '/statistics',
      redirect: (to) => ({ path: '/network', query: { tab: 'overview', ...to.query } }),
    },
    {
      path: '/network',
      name: 'network',
      component: () => import('@/views/NetworkView.vue'),
    },
    {
      path: '/rf-heard',
      name: 'rf-heard',
      component: () => import('@/views/RfHeardView.vue'),
    },
    {
      path: '/stations/:callsign',
      name: 'station',
      component: () => import('@/views/StationView.vue'),
    },
    {
      path: '/logs',
      name: 'logs',
      component: () => import('@/views/LogsView.vue'),
    },
    {
      path: '/map-only',
      name: 'map-only',
      component: MapView,
      meta: { isPopOut: true },
    },
    {
      path: '/stream-only',
      name: 'stream-only',
      component: () => import('@/views/BeaconStreamView.vue'),
      meta: { isPopOut: true },
    },
    {
      path: '/logs-only',
      name: 'logs-only',
      component: () => import('@/views/LogsView.vue'),
      meta: { isPopOut: true },
    },
  ],
})

export default router
