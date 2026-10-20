# Handoff — TASK-016 (Meeting create/list/get API)

Branch: `feature/task-016-persistence` (based on `main` @ 93e6984). Last updated 2026-10-20.

## Done
- TASK-010..015 merged (logging/correlation id, tenancy, memberships/roles, permission policies, Meeting aggregate).
- WIP on this branch:
  - `Application/Identity/ICurrentUser.cs`, `Application/Meetings/` (`MeetingService`, `MeetingContracts`)
  - `Infrastructure/Persistence/` (`KscDbContext`, `EfMeetingRepository`, `AddKscPersistence`) using Npgsql EF Core
  - `Api/Controllers/MeetingsController.cs`, `Api/Authorization/HttpCurrentUser.cs`, wiring in `Program.cs`
  - Dev connection string in `appsettings.Development.json` (`ksc_meeting`, Postgres localhost:5432)
  - `tests/…IntegrationTests/MeetingsApiTests.cs`

## Current state
`dotnet build` passes. `dotnet test`: 18 passed, **7 failed** (all in `MeetingsApiTests`):
- Create returns `400 BadRequest` where `201 Created` is expected.
- Validation failures (blank title, invalid time range) return `400` where tests expect `422`.
- List endpoint response cannot be deserialized to `List<MeetingDto>` (likely returns an object/ProblemDetails because of the 400/auth path).
Likely one root cause in request binding/validation or tenant/user headers in the test setup — start by running one failing test and printing the response body.

## Next steps
1. Fix the 7 failing integration tests (see above).
2. Confirm acceptance for TASK-016: validation and permissions covered by tests; tenant isolation (other tenant cannot list/read); idempotent repeated request.
3. Add the EF migration (`dotnet ef migrations add Initial -p src/KSC.MeetingManagement.Infrastructure -s src/KSC.MeetingManagement.Api`) if not yet created.
4. Open PR to `main`, then pick the next P0 task from `docs/backlog/backlog.json`.

## Setting up on another machine
```
git clone https://github.com/Mohsenbahrzadeh/KSC.MeetingManagement.git
git checkout feature/task-016-persistence
dotnet build && dotnet test
```
Needs .NET SDK 10 (see `global.json`) and, for running the API, a local PostgreSQL.
