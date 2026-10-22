# Meridian Vote Tracker

Imports Assembly vote files, never overwrites approved data silently, sends changes to a human, keeps full history, and saves reproducible Alignment Index snapshots.

**Stack:** ASP.NET Core 8 · EF Core + Npgsql · PostgreSQL (Azure Database for PostgreSQL) · Angular 20 · Docker Compose · Azure Blob, Document Intelligence, Application Insights, Key Vault.

## Run

```bash
cp .env.example .env        # fill in the Azure values (no secrets live in the code)
docker compose up --build
```

| What | URL |
|---|---|
| App | http://localhost:4200 |
| API + Swagger (OpenAPI) | http://localhost:8080/swagger |

On startup the API applies the database migrations and seeds the base data: 150 member states and 3 users. Pick the user in the top bar:
**Dana**, **Omar** (analysts) or **Lena** (admin).

**Azure without keys (Entra ID):** set `Database__Auth=Entra`, `Storage__AccountUrl`, `DocumentIntelligence__Endpoint` (no key) and `Azure__Credential=DeviceCode` (see `.env.example`). On the first start, run `docker compose logs -f api`, open the link it prints and enter the code once. The sign-in is cached in a Docker volume.

**Offline (no Azure):** keep the two "offline" lines from `.env.example` in `.env`, then run `docker compose --profile local up --build`. This adds a local PostgreSQL container. Files are stored on a volume.

**Suggested order:** upload `assembly_export_2024_q1.csv` → `assembly_export_2024_q1_rev2.csv` (creates the conflicts) → `assembly_historic.csv` (scale). Then go to Review → Votes (Lena sets positions) → Snapshots.

### Azure configuration (`.env`)

| Setting | Used for |
|---|---|
| `ConnectionStrings__Meridian` | Azure Database for PostgreSQL (main DB) |
| `Storage__ConnectionString` | Azure Blob: original uploaded CSV/PDF files |
| `DocumentIntelligence__Endpoint` / `__Key` | Bonus: reading scanned voting sheets |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Central logs and request tracing |
| `KeyVault__Uri` (+ `AZURE_*`) | Optional: load all of the above from Key Vault |

### Follow one request end to end (Application Insights → Logs)

Every UI call sends `X-Correlation-Id`, and background jobs keep that ID:

```kusto
union requests, traces
| where timestamp > ago(1h)
| where tostring(customDimensions.CorrelationId) == "<id from the response header or an error message>"
| project timestamp, itemType, name, message
| order by timestamp asc
```

## Run without Docker (development)

```bash
# API (needs a PostgreSQL connection string in user-secrets or env)
cd src/Meridian.Api
dotnet user-secrets set "ConnectionStrings:Meridian" "<connection string>"
dotnet user-secrets set "Storage:Provider" "Local"
dotnet run --urls http://localhost:8080

# Web (proxies /api to :8080)
cd web && npm install && npm start      # http://localhost:4200
```

The typed API client in `web/src/app/api` is **generated** from the API's OpenAPI contract:
building the API writes `web/openapi/Meridian.Api.json`, and `npm run gen:api` (also run by `npm start`/`npm run build`) generates the client from it.

## Tests

```bash
dotnet test        # needs Docker running (integration tests start a real PostgreSQL)
```

- **Unit** (`tests/Meridian.UnitTests`): ingestion rules (the §7.2 example row by row), scoring, PDF parsing and validation.
- **Integration** (`tests/Meridian.IntegrationTests`): the real API, background workers and PostgreSQL (Testcontainers). There is a test for each requirement. Test names start with the requirement ID (ING-xx, CON-xx, HIS-xx, POS-xx, SNP-xx, PDF-xx). They also run the real Q1 files.
- **Scale** (opt-in, about 3 minutes): `MERIDIAN_SCALE_TESTS=1 dotnet test --filter ScaleTests`. It imports the 962k-row history and builds a full-history snapshot.

## Project layout

```
src/Meridian.Core            Pure logic: CSV reader, validation, classification, scoring, PDF sheet parser
src/Meridian.Infrastructure  EF Core model + migrations, background workers, Blob, Document Intelligence
src/Meridian.Api             Controllers, auth (X-User header → roles), ProblemDetails, correlation id
web/                         Angular app (Imports, Review, Votes, Snapshots)
tests/                       Unit + integration tests
```

More: [DECISIONS.md](DECISIONS.md) · [AI_USAGE.md](AI_USAGE.md)
