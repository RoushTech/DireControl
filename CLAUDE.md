# DireControl

## Project Structure

```
DireControl/           Class library — entities, enums, DireControlContext
DireControl.Api/       ASP.NET Core API backend
DireControl.Vue/       Vue 3 / Vite frontend (dev server runs separately)
```

## Running the Project

**Backend** (from repo root):
```bash
dotnet run --project DireControl.Api
# http://localhost:5010
```

**Frontend** (from `DireControl.Vue/`):
```bash
npm run dev
```

**EF Core migrations** (from repo root):
```bash
# Add a migration
dotnet ef migrations add <Name> \
    --project DireControl/DireControl.csproj \
    --startup-project DireControl.Api/DireControl.Api.csproj

# Remove the last migration
dotnet ef migrations remove \
    --project DireControl/DireControl.csproj \
    --startup-project DireControl.Api/DireControl.Api.csproj
```

## .NET / EF Core

- EF Core models live in the `DireControl.Data.Models` namespace under `DireControl/Data/Models/`
- `DireControlContext` lives in the `DireControl.Data` namespace at `DireControl/Data/`
- Enums live in the `DireControl.Enums` namespace under `DireControl/Enums/`
- Each entity implements `IEntityTypeConfiguration<T>` directly on itself (not a nested class); the `Configure` method holds all fluent API config for that model
- Do not add fluent API calls directly in `OnModelCreating`
- The `DbContext` discovers all configs by scanning the assembly: `modelBuilder.ApplyConfigurationsFromAssembly(typeof(DireControlContext).Assembly)`
- JSON columns (e.g. `QrzLookupData`, `WeatherData`, `ResolvedPath`) are stored as `string` / `string?` in the database; the entity exposes a typed, deserialized property and handles serialisation/deserialisation itself — callers always work with the typed value, never the raw JSON string
- All `DateTime` properties are UTC
- Use controllers for all API endpoints — do not use minimal API (`app.MapGet` / `app.MapPost` etc.)
- All UI-facing controllers use the route prefix `api/v0/` — the `v0` prefix signals this is an unsupported UI-only API surface; the sole exception is `HealthController` which stays at `/health`
- Controller request/response models (DTOs) live in `DireControl.Api/Controllers/Models/` under the namespace `DireControl.Api.Controllers.Models` — keep models in that folder, not in a separate `Contracts/` directory
- `appsettings.local.json` is git-ignored and always loaded as an optional override file — use it for local connection strings or secret overrides
- In `Program.cs`, extract `var services = builder.Services` and `var config = builder.Configuration` as the first two lines after `WebApplication.CreateBuilder`, then use `services.` and `config.` exclusively for all subsequent registrations and configuration access — `builder.Services` and `builder.Configuration` must never appear again after the extraction
- Chain service registrations in `Program.cs` into a single `services` chain. When a method enters a sub-builder (`AddControllers`, `AddSignalR`, `AddHttpClient`, etc.), chain that sub-builder's own methods then fold back to `IServiceCollection` via `.Services` to continue the main chain — indent sub-builder methods one extra level so the fold-back is visually obvious. Only break into a separate `services.` call if the sub-builder genuinely has no `.Services` escape hatch. Chain all `app.Use*` middleware together since they all return `IApplicationBuilder`; `app.Map*` calls each stand on their own line because they return different endpoint-builder types with no path back to `IApplicationBuilder`

## Vue / Frontend

- All Axios HTTP calls are made through TypeScript classes located in `src/api` — no Axios requests outside of this directory
- All API files import the shared Axios instance from `src/api/axios.ts` — do not call `axios.create` in individual API files; add interceptors or shared config in `axios.ts`
- CORS is never needed and must never be added — in development the Vite dev server proxies `/api`, `/swagger`, and `/hubs` to `http://localhost:5010`; in production the API serves the built frontend as static files, so all requests are same-origin
- State that is only used within a single component or class stays local; only reach for Pinia when sharing state across components or classes
- Use Vuetify for all UI components
- Path alias `@` resolves to `src/`
- Formatting is handled by `oxfmt`; linting by `oxlint` then `eslint` — run `npm run lint` before committing

## Testing

- Every new APRS packet case (new packet type handled, new parsing path, new message-handling branch) must have a corresponding test in `DireControl.Tests`
- Packet parsing helpers that can be extracted as pure functions belong in `MessageHandlingLogic` (or a similar static helper class) so they can be tested without constructing the full background service
- Use an in-memory SQLite database (via `SqliteConnection("DataSource=:memory:")` + `DbContextOptionsBuilder.UseSqlite`) for tests that exercise DB-level logic — do not use the EF Core in-memory provider, as it does not enforce SQL translation constraints
- Run `dotnet test DireControl.Tests/DireControl.Tests.csproj` before committing; all tests must pass
