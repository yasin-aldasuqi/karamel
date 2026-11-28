# Karamel server

The backend for **reminders while Karamel is closed**. Phones freeze a web page soon after you
leave it, so the page cannot send its own notifications. This server sends a Web Push message at
the right time instead, and the phone's push service (for example Apple's) wakes Karamel to show
it.

> Status: step 5 of 5, hosting, in progress. Done: the foundation, the API, the push sender, the
> website side (see the main README, "Reminders while Karamel is closed"), and everything needed
> to deploy to Fly.io (see [Hosting on Fly.io](#hosting-on-flyio)). Nothing has been sent to a
> real push service yet (see [Known limitations](#known-limitations)).

## What it stores

The server is designed to know as little as possible:

| Table | Holds | Does not hold |
| --- | --- | --- |
| `devices` | the push address the browser gave out, its encryption keys, a SHA-256 hash of the device's secret key, and two timestamps | names, emails, IP addresses, reminder names or messages |
| `reminder_schedules` | a reminder id (such as `posture`), its next time, and its interval | the reminder's name or messages; the device turns the id back into text itself |

- The device's secret key is never stored, only its hash, like a password hash. A leaked database
  does not let anyone change a device's reminders.
- The database enforces its own rules (so they hold even if the code has a bug): one device per
  push address, a hash of exactly 32 bytes, positive intervals, one schedule per reminder per
  device, and deleting a device deletes its schedules (`ON DELETE CASCADE`).

## API

All JSON, all under `/api`.

| Method and path | Auth | What it does |
| --- | --- | --- |
| `GET /api/health` | none | `200 Healthy` when the database answers, `503 Unhealthy` when not. Never rate-limited. |
| `GET /api/push/public-key` | none | `{"publicKey"}`: the server's VAPID public key, which the website needs to subscribe a phone. |
| `POST /api/devices` | none | Body: `{"subscription": <PushSubscription.toJSON()>}`. Returns `201` with `{"deviceId", "secret"}`. |
| `PUT /api/devices/{id}/schedules` | `Bearer <secret>` | Body: `{"schedules": [{"reminderKey", "firesInSeconds", "intervalSeconds"}]}`. Replaces the whole list (empty clears it). `204`. |
| `DELETE /api/devices/{id}` | `Bearer <secret>` | Forgets the device and its schedules. `204`. |

Errors use Problem Details (RFC 9457): `400` with an `errors` object per field, `401` (with
`WWW-Authenticate: Bearer`), `413`, and `429` (with `Retry-After`).

### Design decisions

- **Only real push services as push addresses** (`Push:AllowedEndpointHosts` in
  `appsettings.json`: Apple, Google, Mozilla, Microsoft). The push sender will send requests to
  these addresses, so without the list, anyone could make the server call any URL, such as a cloud
  metadata address (SSRF). Only `https`, port 443, a host name (no IP), and no `user@` part.
  `*.example.com` needs a real subdomain with a dot boundary.
- **The device secret**: 32 random bytes, returned once, stored only as SHA-256. A fast hash is
  fine for a random 256-bit key (slow hashes like bcrypt are for guessable passwords). It is
  compared in constant time. An unknown device and a wrong key get the same `401`, so responses do
  not reveal which ids exist.
- **Relative times**: `firesInSeconds` instead of a clock time. The server adds it to its own
  clock, so a phone with a wrong clock cannot shift its reminders.
- **Replace the whole list**: the app sends all its reminders after any change. One simple,
  idempotent call instead of separate add, change, and remove calls.
- **Registering again**: the same push address keeps its device id, gets a new secret, and loses
  its schedules, so whoever held the old secret no longer controls the device. This is useful
  when the app lost its secret (for example, cleared site data). Anyone registering an address
  must know it, and only the browser and its push service do.
- **Concurrency**, both checked with parallel requests against real PostgreSQL:
  - Registration is one `INSERT … ON CONFLICT (push_endpoint) DO UPDATE` statement, so two
    registrations of one address at the same moment cannot both insert.
  - Replacing schedules first updates the device's `last_seen_at`, which also **locks its row**
    until the commit. A second update for the same device waits, so the result is always one
    whole list. Without the lock, both would insert `posture` and one would fail with a
    duplicate key error (a `500`), which a test showed.
- **Limits**: bodies up to 16 KB (refused by Kestrel before reading), up to 20 schedules, reminder
  keys `^[a-z0-9-]{1,64}\z` (`\z`, because `$` also matches before a final newline), intervals
  from 30 seconds to 1 day, and `firesInSeconds` up to 7 days. The JSON reader refuses missing or
  `null` required fields and duplicated fields.
- **Rate limits** (`RateLimits` in `appsettings.json`, per minute): all requests together (600),
  registrations together (30), and each device (60). Not per IP address, because every request
  arrives through the website's proxy and would share its IP. The overall ceiling also bounds how
  many per-device limiters a flood of made-up device ids can create. The trade-off: a flood of
  registrations can block other registrations for a minute, but cannot reach the database in bulk.
- **Logs**: EF Core logs SQL without parameter values (they show as `'?'`), so secrets, hashes,
  and push addresses do not end up in logs.

## Push sender

A background job (`Push/PushSenderService.cs`) runs every 5 seconds and calls
`Push/PushDispatcher.cs`:

1. **Claim** up to 50 due reminders in **one SQL statement**: `FOR UPDATE SKIP LOCKED` picks rows
   no other sender is busy with, repeating reminders move to their next time, and one-time
   reminders are deleted. It repeats until nothing is due.
2. **Send** each one as a Web Push message with
   [Lib.Net.Http.WebPush](https://github.com/tpeczek/Lib.Net.Http.WebPush). On .NET 6+ it uses
   .NET's own cryptography. The other common library, `WebPush`, still depends on
   `Portable.BouncyCastle` 1.9.0 (2021) even on .NET 10.

Decisions:

- **At most once.** The claim is committed before sending, so even several copies of the server
  never send a reminder twice. The cost: a crash right between the claim and the send skips that
  one firing. Exactly once is not possible over a network; for a reminder, a rare miss is better
  than duplicates.
- **Rhythm.** A repeating reminder stays on its beat (`next + interval`), even if a round ran a
  few seconds late. If the server was down for several firings, it fires once and continues one
  interval from now, like the app does after a long break.
- **The message** is only `{"reminderKey":"posture"}`, encrypted for the phone (`aes128gcm`, RFC
  8291) and signed with the server's VAPID key (RFC 8292, `vapid` scheme, token valid 1 hour).
  The phone turns the key back into the reminder's text. `TTL` 15 minutes, `Urgency: high`, and
  a `Topic` per reminder, so a phone that was offline gets one posture reminder, not one per
  missed firing.
- **Answers from the push service**: `201` sent; `404`/`410` (the phone unsubscribed) forget the
  device and its schedules; `429`, `5xx`, or a network error are retried once, then that firing
  is dropped (the next one comes anyway); anything else (`400`, `403`, `413`) is logged and the
  device is kept. The client never waits for a long `Retry-After`.
- **SSRF, again**: the push address is checked against the allowlist again right before sending,
  and the HTTP client **never follows redirects**, since a redirect could point at an internal
  address. 10-second timeout.
- **Resilience**: an unexpected error in one round is logged and the next round runs as usual.
  (Since .NET 8, an unhandled exception in a background service stops the whole server.)
- **Logs** name the device id and status code only, never push addresses or keys.
- **One at a time.** Pushes in a round are sent one after another. Simple, and plenty for a few
  devices; a bigger app would send in parallel.

### VAPID keys

The server signs every push with its VAPID private key; phones subscribe with the matching public
key. The server refuses to start without a valid, matching pair, or with a `VapidSubject` that is
not an `https` URL or `mailto:` address.

- `dotnet run --project Karamel.Api --no-launch-profile -- generate-vapid-keys` prints a new pair.
- The **private key is a secret**. In development it lives in .NET user secrets (in your home
  folder, outside the repo); when hosted, in an environment variable (`Push__VapidPrivateKey`).
- Changing the keys makes every existing subscription stop working, so the phones have to
  subscribe again.

## Tech

- ASP.NET Core minimal API on **.NET 10 (LTS**, supported until November 2028), not .NET 11
  (short-term support).
- PostgreSQL with EF Core (Npgsql) and snake_case names. The schema lives in committed
  migrations (`Karamel.Api/Data/Migrations`).
- Every endpoint is under `/api`. The website will forward `/api/*` to this server, so the browser
  only talks to the website's own origin: the CSP stays `connect-src 'self'` and no CORS is needed.
- `TreatWarningsAsErrors`, NuGet lock files (`packages.lock.json`), and `dotnet format` in CI.

## Hosting on Fly.io

Chosen for being always on (the sender runs every 5 seconds, so free tiers that sleep would miss
reminders), cheap, and managed (backups included). It runs a standard Docker image, so moving to
another host later needs no code changes.

- **Cost, about $6–7 a month** (checked November 2026): one `shared-cpu-1x` 256 MB machine
  (≈ $2.19) and a Managed Postgres Developer cluster (`dev-256`, $4). That is over the Free plan's
  $5 monthly allowance, and then the whole month is billed.
- **Region:** Frankfurt (`fra`), in the EU.

### The pieces

- `Dockerfile`: builds with the .NET 10 SDK, runs on Microsoft's **chiseled** ASP.NET Core image
  (no shell, no package manager, non-root user). Restores with `--locked-mode`.
- `fly.toml`: always on (`auto_stop_machines = "off"`), HTTPS only, a health check on
  `/api/health`, 256 MB, and the **release command** `migrate`, which updates the database in a
  temporary machine before a new version goes live. If it fails, the deploy stops.
- **Database URL:** Fly gives the database as `DATABASE_URL` (`postgres://user:password@host/db`).
  The server converts it to an Npgsql connection string (`Hosting/DatabaseUrl.cs`): TLS required
  unless the URL says otherwise, and at most 10 pooled connections, because the Developer plan
  allows 20 and Npgsql's default is 100. `ConnectionStrings__Karamel` still wins if set.
- **Logs:** EF Core's SQL logging is at `Warning`, so the sender's query does not fill the logs
  every 5 seconds.
- **CI** builds the image exactly like this, checks that it runs as a non-root user, runs
  `migrate` twice (the second time must change nothing), and starts the server with 256 MB of
  memory to check it stays healthy.

### First deploy

Needs [flyctl](https://fly.io/docs/flyctl/install/) and a Fly.io account with a card. From this
folder:

```bash
fly auth login
fly apps create karamel-api                                         # the name must be free on Fly.io
fly mpg create --name karamel-db --plan dev-256 --region fra        # about $4 a month
fly mpg attach <CLUSTER_ID> -a karamel-api                          # sets DATABASE_URL as a secret

# A new production VAPID key pair, straight into Fly's secrets: never printed, never on disk,
# and never the same as a development key.
dotnet run --project Karamel.Api --no-launch-profile -- generate-vapid-keys | fly secrets import --stage -a karamel-api

fly deploy --ha=false                                               # one machine, not two
curl https://karamel-api.fly.dev/api/health                         # Healthy
```

Then, on Vercel: **Settings → Environment Variables →** `KARAMEL_API_URL` =
`https://karamel-api.fly.dev` (Production and Preview), and redeploy. The website then forwards
`/api/*` to the server and shows "Remind me when Karamel is closed".

### Later

- **Update:** `fly deploy --ha=false` again. The release command applies new migrations first.
- **Logs:** `fly logs -a karamel-api`. **Releases and rollback:** `fly releases -a karamel-api`.
- **Changing the VAPID keys** makes every phone's subscription stop working until it subscribes
  again, so do it only if the private key leaked.

## Run it locally

Needs the .NET 10 SDK and PostgreSQL.

```bash
cd server
dotnet tool restore                      # the EF Core command-line tool (dotnet-ef)
dotnet ef database update -p Karamel.Api # create the tables

# Once: a VAPID key pair, kept in user secrets (outside the repo).
dotnet run --project Karamel.Api --no-launch-profile -- generate-vapid-keys
dotnet user-secrets set "Push:VapidPublicKey" "<public key>" -p Karamel.Api
dotnet user-secrets set "Push:VapidPrivateKey" "<private key>" -p Karamel.Api

dotnet run --project Karamel.Api         # http://localhost:5080
curl localhost:5080/api/health           # "Healthy", or 503 "Unhealthy" without a database
```

The development connection string is in `Karamel.Api/appsettings.Development.json`
(port 55432, user `karamel`, no password). Elsewhere, set the environment variable
`ConnectionStrings__Karamel`. The app refuses to start without it.

## Tests

```bash
cd server
dotnet test
```

The tests use **a real PostgreSQL**, not an in-memory fake, because the fakes skip constraints,
unique indexes, and locking. Each test class creates its own database, builds it with the real
migrations, and drops it afterwards. Point them at a server with the environment variable
`KARAMEL_TEST_POSTGRES` (default `Host=localhost;Port=55432;Username=karamel`; the user needs the
`CREATEDB` right). In CI, a `postgres:18` service container is used.

- `HealthTests`: healthy with the database, 503 without it (the real app, in memory).
- `SchemaTests`: the migrations match the model, and every database rule above, checked by
  asserting PostgreSQL's own error code and constraint name.
- `RegisterTests`: registering stores only the secret's hash; every push service is accepted;
  15 addresses outside them are refused (SSRF cases); malformed keys and JSON are refused;
  registering again rotates the secret; 30 simultaneous registrations of one address leave one
  device with exactly one working secret.
- `AuthTests`: no header, a malformed or short key, a wrong key, another device's key, an unknown
  device, and the Basic scheme all get `401` and change nothing.
- `ScheduleTests`: times come from the server's (fake) clock, each update replaces the whole list,
  12 kinds of invalid lists are refused and change nothing, 40 simultaneous updates end with one
  whole list, and forgetting a device deletes everything.
- `LimitTests`: registration, per-device, and overall rate limits; and on the real Kestrel server,
  bodies over 16 KB get `413`.
- `PushSenderTests`, with a fake push service (`FakePushService`, nothing leaves the machine)
  and a fake phone (`TestBrowser`):
  - The fake phone **decrypts** each message and **verifies** its VAPID signature with code written
    from RFC 8291, 8188, and 8292 using .NET's own cryptography, independently of the library.
    So the library's output is checked against the standards, not against itself.
  - Due and not-yet-due reminders, the rhythm, one-time reminders, missed firings collapsing into
    one, `404`/`410` forgetting the device, one retry for `503`/`500`/`429`/network errors, no
    retry for `403`, and an address no longer on the allowlist getting nothing.
  - **Four senders at once** send 200 reminders exactly once each. A trigger in the test database
    slows every row change by 2 ms, so the senders' statements really overlap.
  - A reminder another sender holds is **skipped, not waited for** (`SKIP LOCKED`), and sent once
    the other sender lets go.
  - The background job runs on its timer, and survives a round that crashes.
  - Startup refuses missing, mismatched, or malformed keys and a bad subject; the push client does
    not follow redirects.

Each guarantee was checked by breaking it on purpose (31 times so far): for example, removing the
row lock, the `ON CONFLICT`, the dot boundary for `*.` hosts, the `user@`/port check, the hash
(storing the secret itself), the constant-time comparison, `\z`, the JSON strictness options, the
body size limit, or the overall rate limit each make a test fail. The tests also ran three times in
a row without the row lock (failed every time) and three times with it (passed every time). In
step 3, the 14 breaks included no row locking (the several-senders test failed 3 runs out of 3),
no `SKIP LOCKED`, a lost rhythm, replaying missed firings, never deleting one-time reminders,
keeping unsubscribed phones, no retry, treating `429` as permanent, no allowlist check before
sending, following redirects, accepting mismatched keys, no `Topic`, a crash stopping the sender,
and a message carrying more than the reminder id.

Two lessons from breaking things on purpose here: the first version of the several-senders test
passed even with **no locking at all**, because the senders never overlapped. It now forces the
overlap. And a deliberate break can itself be wrong: PostgreSQL runs a `DELETE` inside `WITH` even
when nothing reads its result, so the first attempt at "never delete one-time reminders" changed
nothing.

## Known limitations

- **Not yet tested against a real push service.** The fake phone proves the messages follow the
  standards, but Apple's, Google's, and Mozilla's servers have only been read about, not talked
  to. That happens in step 5, on a real iPhone.
- **At most once**: a crash between claiming and sending skips that firing (see above).
- **Sequential sending**: fine for a few devices, slow for thousands.
- **Changing the VAPID keys** breaks all existing subscriptions until the phones subscribe again.
