# KSC.MeetingManagement

Multi-tenant meeting-management system (.NET 10, ASP.NET Core API, Blazor Web, EF Core + PostgreSQL).

## Layout
- `src/…Domain` — aggregates (Meeting, memberships, roles); no dependencies.
- `src/…Application` — services, contracts, ports (`ICurrentUser`, `IMembershipStore`, `ITenantStore`).
- `src/…Infrastructure` — EF Core (`Persistence/`), in-memory tenant/membership stores.
- `src/…Api` — controllers, tenant-resolution middleware, permission policies, correlation id, ProblemDetails.
- `src/…Web`, `src/…Calendar` — Blazor UI and calendar.
- `tests/…UnitTests`, `tests/…IntegrationTests` (WebApplicationFactory + EF InMemory).

## Conventions
- Work is tracked by `TASK-NNN` ids in `docs/backlog/backlog.json`; one feature branch per task (`feature/task-NNN-…`), merged by PR into `main`.
- Architecture decisions live in `docs/adr/` (ADR-0002 database, ADR-0003 authentication, …). Read them before changing those areas.
- Every request is tenant-scoped; never query across tenants. Authorization uses permission policies (`PermissionPolicyProvider`).
- Backlog/ADR text is in Persian; code, identifiers and commit messages are in English.

## Commands
```
dotnet build
dotnet test
```
See `docs/HANDOFF.md` for the current in-progress state.
