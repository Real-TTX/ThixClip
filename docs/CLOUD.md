# ThixClip.Cloud

ThixClip.Cloud is the server of ThixClip. It keeps the clipboard history of an account (texts, links, images with the text recognised in them),
syncs it between all PCs and Macs that are signed in to the account, and shows it in the browser as a Progressive Web App. Deleting an entry
anywhere deletes it everywhere.

Stack: ASP.NET Core (.NET 10), Razor Pages with **MatUi** (copied from MatDesign into `wwwroot/matui`, never edited by hand), EF Core +
**PostgreSQL 17**, SkiaSharp for images. Docker-first, port **4950**. UI language German, code English.

## Run it

| File | Stack | What | Addresses |
|---|---|---|---|
| `docker-compose.yml` | `thixclip` | Release (branch `main`), image `ghcr.io/real-ttx/thixclip:latest` | app http://localhost:4950 |
| `docker-compose.dev.yml` | `thixclip-dev` | Development (branch `dev`), image `:nightly`, plus pgAdmin | app :4950, pgAdmin :4951 |
| `docker-compose.local.yml` | `thixclip-local` | Built from this folder (`build: .`), plus pgAdmin, Postgres published | app :4950, pgAdmin :4951, Postgres :4952 |

```
docker compose up -d                                   # release
docker compose -f docker-compose.dev.yml up -d         # nightly
./scripts/redeploy.ps1                                 # local: rebuild the image and restart the local stack (-Clean also drops the volumes)
```

No variables, no `.env`: the app's default connection string (`Host=db;Port=5432;Database=thixclip;Username=thixclip;Password=thixclip`) fits the
compose files. `THIXCLIP_DB` may override it (not used by the compose files). Volumes: `web-data` (`/data`: the keys that protect the sessions)
and `db-data` (the database). The first visit opens the **setup assistant**, which creates the administrator. For use over the internet put a
TLS proxy in front (Caddy, nginx, Traefik; `X-Forwarded-*` are honoured).

`dotnet run --project src/ThixClip.Cloud` (environment Development) uses the Postgres of the local stack on port 4952 and keeps its keys in
`artifacts/cloud-data`.

## Pages

- **Einrichten** (`/Account/Setup`, only on a fresh installation), **Anmelden** / Abmelden.
- **Verlauf** (`/Clips`): the clips grouped by day; the toolbar above has the search (also in the text recognised in pictures; `/` jumps into it),
  Filter (Typ: Text, Link, Bild; Markiert: Favoriten, Angeheftet) and Sortieren (zuletzt / zuerst kopiert, newest or oldest first); the pager under the
  list; the actions under it: **Neuer Eintrag** … gap … **Verlauf leeren** (keeps favourites and pinned clips, asks first). The star of a row toggles
  the favourite. The list **updates itself** (`js/live.js` long-polls `/live/revision`) when a device, another browser or the retention changes something.
  The menu has the same views as entries (Bilder, Links, Favoriten, Angeheftet).
- **Eintrag** (`/Clips/Edit?id=`): text clips have an editable text field (a change reaches all devices), image clips the picture (no text field),
  the recognised text with a copy button, the switches Favorit / Angeheftet, the facts (type, OCR, size, dates, source, until when it is kept).
  Buttons: Speichern, Zurück, Kopieren (text through MatUi `data-copy`, a picture as PNG through `ClipboardItem`, `js/app.js`), Herunterladen (pictures),
  gap, Löschen.
- **Neuer Eintrag** (`/Clips/Edit`): Art (Text oder Link / Bild); the text field or the upload is shown, not both. It appears on every device at once.
- **Konto** with tabs: **Allgemein** (`/Account`: display name, Speicherdauer 1/7/30/90/365 days or unbegrenzt — for all devices; a shorter time
  removes older clips at once), **Sicherheit** (`/Account/Security`: password; after a password set by an administrator the person must choose one
  first), **Geräte** (`/Account/Devices`: list with toolbar → `/Account/Device?id=`: rename, **Entfernen** ends the device's token at once).
- **Administration → Benutzer** (`/Admin/Users`, administrators only): list with search, filter (role, status), sort, pager; `/Admin/Users/Edit`:
  create, change (role, may sign in, new password), delete (removes the account's clips and devices for good). The business layer checks the role again.
- **Über** (`/About`) through the version number at the bottom left of the menu. Errors (`/Error?code=`) without the menu.

An administrator has no access to the clips of other accounts (the business layer only ever reads the actor's own clips).

## The sync API (`src/ThixClip.Shared/SyncContract.cs`)

JSON (camelCase, enums as text, nulls left out); errors are `ApiError { message, field }` with a German message. Every answer carries
`X-ThixClip-Protocol: 1`; a device that sends a higher protocol gets 400. Every route but the login needs `Authorization: Bearer <device token>`;
an unknown or removed device gets **401**.

| Route | What |
|---|---|
| `POST /api/v1/devices/login` | `DeviceLoginRequest` → `DeviceLoginResponse` (token shown once). 401 for a wrong name/password, 10 attempts a minute per address (429). The same device name + platform signing in again replaces its old entry. |
| `POST /api/v1/devices/logout` | ends the token (204) |
| `GET / PUT /api/v1/account` | `AccountInfo`; PUT `{ retentionDays }` (one of 1, 7, 30, 90, 365, 0) |
| `GET /api/v1/clips/changes?since=&limit=` | `ChangeSet`: everything above `since` in revision order (tombstones included), at most `limit` (≤ 200); `latest` = revision of the last clip in the set (or `since`); ask again while `hasMore` |
| `GET /api/v1/clips/wait?since=&timeout=` | long poll (≤ 30 s): answers `{ latest }` as soon as the account has a revision above `since` |
| `POST /api/v1/clips/push` | `PushRequest` (≤ 200 clips) → `PushResponse { results, latest }`; a bad clip refuses the whole push (400, `field` = `clips[i].text` …) |
| `PUT /api/v1/clips/{syncId}/image` | body = the picture (PNG; other formats are converted), ≤ 25 MB → `{ revision }` |
| `GET /api/v1/clips/{syncId}/image`, `/thumbnail` | the PNG / a preview (longest side 320 px) |

Sync rules:
- Every change on the server (push, image upload, a change in the browser, the retention, "Verlauf leeren", a deleted account) takes the next number
  of **one** Postgres sequence `ClipRevision`, in a transaction that holds the account's advisory lock: within an account revisions are committed in
  order, so "everything above N" never misses a change. After the commit the long polls of the account are woken (in memory; one server instance).
- A clip is identified by its `SyncId`. A push of a known SyncId applies only when its `updatedAt` is not older than the last change (last writer wins).
- **Deleting wins**: a deleted clip stays a tombstone (SyncId + revision; text, OCR text, hash and picture are wiped); later pushes for it answer `isDeleted`.
- A **new** SyncId whose `contentHash` the account already has (live clip): no second clip; the existing one moves to the top (copied date), favourite/pinned
  are added, and the answer has `mergedInto` = the existing SyncId (the device re-keys its local clip).
- `contentHash` = SHA-256 hex upper case of the text as UTF-8 (exactly the text, no normalisation) or of the PNG bytes. For text clips the server checks it.
- `needsImage` = an image clip without its PNG on the server: upload it with PUT image.
- The source of a new clip is the name of the signed-in device (as it is in the account), "Web" for the browser.
- Retention: every hour (and at once when the setting changes) clips copied before the account's retention become tombstones, except favourites and pinned ones.

## Data model (PostgreSQL)

Tables and columns PascalCase, key `Id` BIGINT, every row `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`; soft delete `UpdateState`
(0 deleted, 1 created, 2 updated). Migrations in `src/ThixClip.Cloud/Data/Migrations` are applied at start-up
(`dotnet ef migrations add <Name> -p src/ThixClip.Cloud -o Data/Migrations`; a design-time factory exists).

| Table | Columns (besides the audit columns) |
|---|---|
| `User` | UserName (unique among live accounts), DisplayName, PasswordHash (PBKDF2), IsAdmin, IsActive, MustChangePassword, RetentionDays, Theme, FailedLogins, LockoutUntil, LastLoginDate, UpdateState |
| `UserSession` | **Token** (uuid, the cookie carries only it), UserId, LastSeenDate, ExpiresDate, IsRevoked, UserAgent, IpAddress |
| `Device` | **Token** (uuid, the bearer token), UserId, Name, Platform, AppVersion, LastSeenDate, IpAddress, UpdateState |
| `ClipEntry` | UserId, SyncId (uuid, unique per account), Kind (1 Text, 2 Link, 3 Image), Text, OcrText, ContentHash, IsFavorite, IsPinned, CopiedDate, ClientCreateDate, ClientUpdateDate, Source, HasImage, ImageWidth, ImageHeight, ImageBytes, Revision, UpdateState |
| `ClipImage` | ClipEntryId (unique), Png, Thumbnail (bytea; deleted with the tombstone) |
| sequence `ClipRevision` | the revisions of all clips |

## Code

```
src/ThixClip.Cloud
  Program.cs            composition root (cookie auth, rate limit "signin", JSON of the contract, static files, endpoints)
  Infrastructure.cs     HealthCheck (--healthcheck), Database.MigrateWithRetry, SetupRedirect, ForcePasswordChange, SecurityHeaders (CSP without inline script/style), MaintenanceService (hourly: sessions, retention)
  Data/                 Entities, AppDbContext (audit columns, soft delete), Migrations
  Business/             THE business layer, static classes, each takes the AppDbContext and the Actor and checks what the actor may do:
                        ClipService (browser), SyncService (devices), DeviceService, AccountService, UserService (setup, sign-in, administration),
                        SessionService, Revisions (+ RevisionSignals: the long polls), ImageTools (SkiaSharp), ClipContent (hash, links), Passwords
  Api/                  SyncEndpoints (/api/v1, bearer), LiveEndpoints (/live: long poll and pictures for the browser, cookie)
  Web/                  CurrentUser, CookieSignIn, Ui (icons), Assets (sprites), ListModels (toolbar, pager), Dates, Menu
  Controls/             one code base per control: <tc-field>, <tc-buttons>, <tc-table>, <tc-tabs>/<tc-tab>; partials _Toolbar, _Pager, _FormSummary, _Empty
  Pages/                Razor Pages (they call the business layer only, never the database)
  wwwroot/              matui/ (toolkit), css/app.css (accent #2F6FED, tc-*), js/ (app, live, offline, pwa), sw.js, manifest.webmanifest, offline.html, robots.txt, llms.txt, img/
tests/ThixClip.Cloud.Tests   unit tests; business layer and API against PostgreSQL when THIXCLIP_TEST_DB is set
tests/e2e                    browser checks (Playwright + installed Edge) against a running stack
```

## PWA and offline

`manifest.webmanifest` (standalone, maskable icon, shortcuts), `sw.js`: the shell is cached (cache first, refreshed in the background); the history
and clip pages are network first with a copy (shown offline and marked: the top bar says "Offline"); previews of pictures are kept; every other page
offline shows `offline.html`, which reloads by itself on the `online` event and every 10 s, and has "Erneut versuchen". The long poll, the API and
every POST are never cached. Signing out clears the copies. iOS: `viewport-fit=cover`, safe areas (MatUi + `app.css`), status bar colour = page
background in light and dark (`theme-color` per scheme), 16 px fields on touch screens (no zoom into fields), pinch zoom stays.

## Test it

```
dotnet build src/ThixClip.Cloud                     # 0 warnings
dotnet test tests/ThixClip.Cloud.Tests              # unit tests; with THIXCLIP_TEST_DB also the business layer and the API
$env:THIXCLIP_TEST_DB = "Host=localhost;Port=4952;Database=postgres;Username=thixclip;Password=thixclip"   # the Postgres of the local stack
./scripts/redeploy.ps1                              # local stack on :4950
cd tests/e2e; npm install; node run.js http://localhost:4950   # browser checks + screenshots in tests/e2e/shots (look at them)
```

`run.js` goes through the setup assistant on a fresh stack (or signs in), creates the second account, signs in two devices through the API, pushes
texts and pictures (with OCR text), checks the merge, the live list, the search in OCR text, create/delete in the browser (tombstone for the device),
removing a device (401), 390 px and dark mode, offline and back online, and the forced password change.

### Local test accounts (local development stack only)

| Account | User name | Password | Note |
|---|---|---|---|
| Administrator | `admin` | `ThixClip-Admin-2026` | created through the setup assistant |
| User | `anna` | `Anna-Test-2026` | created by the administrator; must choose a new password at the first sign-in |

These are test values of the local stack (`docker-compose.local.yml`), never for a real installation.
