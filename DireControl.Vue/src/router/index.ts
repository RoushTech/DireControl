import { createRouter, createWebHistory } from 'vue-router'
import MapView from '@/views/MapView.vue'

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
      path: '/beacons',
      name: 'beacons',
      component: () => import('@/views/BeaconStreamView.vue'),
    },
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
    {
      path: '/aprs-icon-test',
      name: 'aprs-icon-test',
      component: () => import('@/views/AprsIconTest.vue'),
    },
    {
      path: '/alerts',
      name: 'alerts',
      component: () => import('@/views/AlertsView.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/views/SettingsView.vue'),
    },
    {
      // Frequencies merged into Statistics — redirect old bookmarks.
      path: '/frequencies',
      redirect: '/statistics',
    },
    {
      path: '/statistics',
      name: 'statistics',
      component: () => import('@/views/StatisticsView.vue'),
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
