# NukiApiProbe

Console tool that checks what the Nuki Web API lets this account do. It is separate from AirBnBWorkflow and shares no code or settings with it.

| Step | Call | Changes the lock? |
|---|---|---|
| 1. Token check | `GET /account` | No |
| 2. List locks | `GET /smartlock` | No |
| 3. Read entries | `GET /smartlock/{id}/auth` | No (PINs are not printed) |
| 4. Write test | `PUT`, `POST`, `DELETE /smartlock/{id}/auth` | Yes, after a `y/N` prompt |

The write test creates a keypad code named `API-TEST` that is valid for only one day a year from now, so it can't open the door. It renames that code, then pauses and asks before deleting it so you can look at the entry in Nuki Web. The lock is read back after each step to confirm the change. If an earlier run left a test entry behind, the probe offers to delete it at the start of step 4.

## Setup

Create an API token in Nuki Web and give it full lock and authorization permissions. Then store it from this folder:

```
dotnet user-secrets set "Nuki:ApiToken" "<your token>"
```

The token is saved under `%APPDATA%\Microsoft\UserSecrets`, outside this folder. Setting the `NUKI_API_TOKEN` environment variable also works and takes priority.

## Run

```
dotnet run                       # all steps (asks before step 4)
dotnet run -- --read-only        # steps 1-3 only
dotnet run -- --lock <id>        # pick the lock when the account has several
```

The exit code is 0 if every step passed and 1 if any step failed.

Status codes that answer the question:
- **402** (account not paid) or **426** (account upgrade required) on create: Nuki still restricts this for your account.
- **403**: the token is missing a permission.
