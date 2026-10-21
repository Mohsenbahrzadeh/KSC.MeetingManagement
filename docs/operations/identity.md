# Sign-in, first setup and user management

## Settings (environment variables or secret storage, never committed)

| Setting | Meaning |
|---|---|
| `Auth__SigningKey` | JWT signing key, at least 32 random characters. Without it sign-in returns 503 and no token is accepted. |
| `Auth__Issuer`, `Auth__Audience` | Optional, default `ksc-meetings`. |
| `Setup__Token` | One-time installation secret, at least 16 characters. Without it `/api/v1/setup` does not exist (404). Remove it after setup. |

## First installation

```
POST /api/v1/setup        header: X-Setup-Token: <Setup__Token>
{ "tenantName": "...", "slug": "acme", "timeZoneId": "Asia/Tehran",
  "email": "owner@example.com", "displayName": "...", "password": "at least 10 characters" }
```

Works only while the system has no organisation (otherwise 409). Creates the organisation and its first user with the `TenantOwner` role.

## Sign-in

`POST /api/v1/auth/login` returns a 15 minute access token and a 14 day refresh token (rotating; reusing an old one revokes all sessions of that user).
Five wrong passwords lock the account for 15 minutes. Send `Authorization: Bearer <token>` plus `X-Tenant-Id` on tenant endpoints.

## Managing members (permission `users.manage`)

- `GET /api/v1/users`, `POST /api/v1/users` (new email needs a password; an existing account only gains a membership)
- `PUT /api/v1/users/{id}/roles`, `PUT /api/v1/users/{id}/active`
- Nobody can change their own membership; only a `TenantOwner` can grant, remove or modify the owner role. All changes are audited.

## Not verified here

Checked by hand on PostgreSQL 17.6 (2026-10-21): all migrations including `AddIdentity` apply; setup, login, meeting create (idempotent repeat, conflict 409, validation 422, tenant 403), reschedule and optimistic-concurrency (two parallel postpones: one 200, one 409) behave as expected.
Also checked: refresh-token rotation (reusing an old token returns 401 and revokes the newer one), member creation (201) and account lockout (429 after repeated wrong passwords, even with the correct password while locked).
The whole integration suite (253 tests) also passed against PostgreSQL 17.6 (2026-10-21) with a fresh, fully migrated database per test: `KSC_TEST_POSTGRES="Host=localhost;Database=postgres;Username=postgres;Password=...;Pooling=false" dotnet test tests/KSC.MeetingManagement.IntegrationTests` (run in chunks of about 60 tests; a single run of all of them timed out on connections). This covers role changes, deactivation, agenda, minutes, audit and reports functionally; concurrent-load behaviour of transactions and unique indexes is still unchecked.

## Web application

The Blazor app shows `/login` to anonymous visitors (other pages redirect there and come back afterwards). The refresh token is kept encrypted in the
session storage of the browser tab (it disappears when the tab closes); the access token stays in server memory and is renewed shortly before it expires.
Users with several organisations pick one from the account menu; every API call then carries that organisation in `X-Tenant-Id`.
`Api:TenantId` is no longer used.

Notification addresses (reminders, invitations to members) are now read from the user accounts of active members.
