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
- `appsettings.local.json` is git-ignored — use it for local connection string or secret overrides

## Vue / Frontend

- All Axios HTTP calls are made through TypeScript classes located in `src/api` — no Axios requests outside of this directory
- State that is only used within a single component or class stays local; only reach for Pinia when sharing state across components or classes
- Use Vuetify for all UI components
- Path alias `@` resolves to `src/`
- Formatting is handled by `oxfmt`; linting by `oxlint` then `eslint` — run `npm run lint` before committing
