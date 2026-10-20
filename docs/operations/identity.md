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

The `AddIdentity` migration and these flows have run only on the EF in-memory provider, not on a real PostgreSQL.
