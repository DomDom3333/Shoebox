# 📸 GroupPhoto

A lightweight, self-hosted web app for collecting everyone's photos from a shared event —
a wedding, a festival, a holiday trip — in one place. **No accounts, no app**: you share a
link (or QR code), people open it on their phone, type their name, and upload.

## Features

- **Pools** — one shared gallery per event, reachable by short code or link (unlisted by default)
- **Optional password** — guests enter it once per device; after that every page, image and
  download is unlocked via a signed cookie (image URLs are useless without it)
- **No accounts** — uploaders just type a name; a browser cookie remembers who they are
- **Gallery** — fast thumbnail grid (server-side WebP thumbs), lightbox, filter by uploader,
  photos sorted by when they were taken (EXIF)
- **Downloads** — single photos, the whole pool as a streamed ZIP, or **"download others'"**:
  everything *except* your own uploads
- **QR code sharing** — print it on the wedding tables, tape it to the festival cooler
- **Admin link** — the pool creator gets a private management link: delete photos, change
  the password, set auto-expiry, or delete the pool
- **Auto-expiry** — optionally delete a pool N days after the event
- **Duplicate detection** — the same photo uploaded twice is stored once (SHA-256)
- Storage is just **files on disk + a SQLite database** — trivial to back up

## Quick start (Docker)

```bash
docker compose up -d --build
# open http://localhost:8080
```

All state (photos, database, cookie signing keys) lives in the `groupphoto-data` volume
mounted at `/data`.

### Configuration

Set via environment variables (or `appsettings.json`):

| Variable | Default | Purpose |
|---|---|---|
| `GroupPhoto__DataPath` | `/data` (in Docker) | Root folder for DB, photos and keys |
| `GroupPhoto__MaxFileSizeMb` | `50` | Per-file upload limit |
| `GroupPhoto__ThumbnailSize` | `480` | Longest edge of gallery thumbnails (px) |
| `GroupPhoto__CookieLifetimeDays` | `90` | How long unlock/identity cookies last |
| `GroupPhoto__PublicBaseUrl` | *(derived from request)* | Public URL used in share links & QR codes |

### Behind a reverse proxy

The app honours `X-Forwarded-Proto`/`X-Forwarded-For`, so HTTPS termination in
Caddy/nginx/Traefik works out of the box. Set `GroupPhoto__PublicBaseUrl` to your public
address so QR codes and share links are correct, and make sure your proxy's upload body
limit is at least `MaxFileSizeMb` (e.g. `client_max_body_size` in nginx).

## Run locally (development)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/GroupPhoto.Web
# open the URL it prints (default http://localhost:5000)
```

Data is written to `src/GroupPhoto.Web/data/` (gitignored).

## How access control works

- Pools are unlisted; you need the 8-character code (or link) to find one.
- If a pool has a password, the browser must unlock it once. Unlocking sets a
  **tamper-proof cookie** (ASP.NET Data Protection). Every image, thumbnail and ZIP
  request re-checks that cookie server-side — photo files are stored outside the web
  root and are never served statically, so copying an image URL doesn't bypass the
  password.
- The pool creator receives a separate **admin link** containing a secret key. Anyone
  with that link can manage the pool — treat it like a password.
- Uploaders are identified by a random ID in a long-lived cookie. That's what powers
  the "yours" badges, "delete my photo" and "download others'" — it is not a security
  boundary, just a convenience.

## Notes & limitations

- **HEIC/HEIF** (iPhone) files are accepted and stored, but no thumbnail/preview is
  generated for them — they show as a placeholder tile and are included in ZIPs.
- **EXIF data (including GPS location) is preserved** on the original files. Everyone in
  the pool can download originals — mention this to privacy-conscious guests.
- One instance, one filesystem: this is deliberately simple software for a party, not a
  photo platform. Back up `/data` and you've backed up everything.

## Tech

ASP.NET Core (Razor Pages, .NET 10) · EF Core + SQLite · SixLabors.ImageSharp · QRCoder ·
vanilla JS/CSS frontend · Docker multi-stage build.
