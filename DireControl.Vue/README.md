# DireControl UI

Vue 3 + Vite + Vuetify front end for DireControl. See the repository root
`README.md` for what the app does and how to run the whole stack.

## Development

```bash
npm install
npm run dev      # Vite dev server; proxies /api and /hubs to http://localhost:5010
```

Start the backend first (`dotnet run --project DireControl.Api` from the repo root).

## Checks

```bash
npm run lint     # oxlint, then eslint
npm run format   # oxfmt
npm run build    # vue-tsc type-check + production build
```

## Conventions

- HTTP calls go through the classes in `src/api`, which share the Axios
  instance in `src/api/axios.ts`; SignalR connections are built with
  `src/api/hubConnection.ts`.
- UI components are Vuetify. Pinia only for state shared across components.
- `@` resolves to `src/`.
