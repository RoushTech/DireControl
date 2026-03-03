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
      path: '/statistics',
      name: 'statistics',
      component: () => import('@/views/StatisticsView.vue'),
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
  ],
})

export default router
