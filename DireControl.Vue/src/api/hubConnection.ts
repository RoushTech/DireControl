import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'

/**
 * Builds a SignalR connection to one of the backend hubs with the app-wide
 * policy: automatic reconnect and warning-level client logging.  Each hub
 * (/hubs/packets, /hubs/terminal, /hubs/audio, /hubs/logs) is distinct, so
 * each store owns its own connection — this only keeps them configured alike.
 */
export function createHubConnection(path: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(path)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
}
