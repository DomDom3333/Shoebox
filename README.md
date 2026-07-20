# GroupPhoto

A lightweight, self-hosted web app for collecting everyone's photos from a shared event into
one pool — no accounts, no app. Share a link (or QR code), and people open it on their phone,
type their name, and upload. Useful for weddings, festivals, and trips, where the good photos
end up scattered across a dozen phones.

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

> [!IMPORTANT]
> GroupPhoto is built for casually sharing event photos — not for sensitive data, and not as
> durable storage.
>
> - **Security is deliberately lightweight.** Access is gated only by unguessable pool links
>   and an optional shared password; there are no accounts and no hardening beyond that.
> - **Files are stored unencrypted** on the server's filesystem. Anyone with access to the
>   host — or to its backups — can read every uploaded image. Only use a server you trust, and
>   don't upload anything you'd mind others seeing.
> - **It is not durable.** A single instance on a single filesystem, with no replication,
>   versioning, or off-site backup, and pools can be set to delete themselves.
>
> Treat it as a convenient drop box: gather photos, then have people download what they want to
> keep. Back up the data directory yourself if a pool matters.

## How it works

For the organizer:

1. Create a pool, give it a name, and optionally set a password and an auto-delete date.
2. You get a share link and QR code to hand out, plus a private admin link to keep.

For everyone else:

1. Open the link (or scan the QR code) — no sign-up.
2. Type your name and drag in photos.
3. Browse the gallery and grab everyone else's photos as a ZIP.

## Features

- **Shareable pools** — one gallery per event, reachable by an 8-character code, a link, or a QR code. Pools are unlisted: there is no directory and no way to browse other people's events.
- **No accounts** — uploaders just enter a name. A browser cookie remembers who they are, so their photos get a "you" badge and they can delete their own uploads.
- **Optional passwords** — a guest enters the password once per device; a signed cookie unlocks the pool after that. Photo files live outside the web root and every image and download re-checks the cookie server-side, so a leaked image URL is useless without it.
- **Fast gallery** — a WebP thumbnail grid plus a full-screen lightbox backed by a downscaled web-safe proxy, so viewing is sharp without sending a full-size original over the wire. Filter by uploader; photos sort by capture time (EXIF).
- **Flexible downloads** — a single photo, the whole pool as a streamed ZIP, or "download others'": everything except your own uploads.
- **Private admin link** — the creator can rename the pool, change or remove the password, adjust expiry, delete individual photos, or delete the whole pool.
- **Auto-expiry** — a pool can be set to delete itself a chosen number of days after the event.
- **Deduplication** — the same file uploaded twice is stored once (SHA-256).
- **Simple storage** — files on disk plus a SQLite database. One directory holds everything.

## Quick start

With Docker, which is the easiest way to run it:

```bash
docker compose up -d --build
# open http://localhost:8080
```

All state (photos, database, cookie-signing keys) is kept in the `groupphoto-data` volume
mounted at `/data`.

### Run locally for development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/GroupPhoto.Web
# open the URL it prints (e.g. http://localhost:5225)
```

Data is written to `src/GroupPhoto.Web/data/` (gitignored).

## Configuration

Set via environment variables (`GroupPhoto__Key`) or the `GroupPhoto` section of
`appsettings.json`:

| Setting | Default | Purpose |
|---|---|---|
| `DataPath` | `/data` (Docker), `data` (local) | Root folder for the database, photos, and keys |
| `MaxFileSizeMb` | `50` | Per-file upload limit |
| `ThumbnailSize` | `480` | Longest edge of gallery thumbnails (px) |
| `DisplaySize` | `1600` | Longest edge of the lightbox proxy (px) |
| `DefaultExpiryDays` | `0` | Expiry pre-selected on the create form (`0` = never) |
| `CookieLifetimeDays` | `90` | How long unlock, identity, and admin cookies last |
| `PublicBaseUrl` | *(derived from request)* | Public URL used in share links and QR codes |

### Behind a reverse proxy

The app honours `X-Forwarded-Proto` and `X-Forwarded-For`, so HTTPS termination in
Caddy, nginx, or Traefik works out of the box. Two things to set:

- `GroupPhoto__PublicBaseUrl` — your public address, so QR codes and share links are correct.
- Your proxy's request-body limit — at least `MaxFileSizeMb` (for example `client_max_body_size 50m;` in nginx).

## Supported formats

Uploads are accepted and decoded server-side (Magick.NET) in these formats:

| Format | Extensions |
|---|---|
| JPEG | `.jpg`, `.jpeg` |
| PNG | `.png` |
| GIF | `.gif` |
| WebP | `.webp` |
| HEIC / HEIF | `.heic`, `.heif` |

Every accepted upload gets a WebP thumbnail and lightbox proxy regardless of source format, so
formats that browsers can't display natively — HEIC/HEIF from phones in particular — still
appear in the gallery everywhere. The original file is always stored unmodified and is what the
Download button returns. Other file types are rejected at upload, and files above
`MaxFileSizeMb` are rejected too.

## Under the hood

### Three renditions per photo

Each upload is decoded once and produces three files, so every context gets a right-sized image:

| Rendition | Size | Format | Used for |
|---|---|---|---|
| Thumbnail | ~480px | WebP | Gallery grid |
| Display proxy | ~1600px | WebP | Full-screen lightbox |
| Original | untouched | as uploaded | Downloads and ZIPs |

### Storage layout

```
/data
├── groupphoto.db                     # SQLite: pools + photo metadata
├── keys/                             # Data Protection keys (signed cookies)
└── pools/{poolId}/
    ├── orig/{photoId}.{ext}          # untouched originals
    ├── thumb/{photoId}.webp          # grid thumbnails
    └── display/{photoId}.webp        # lightbox proxies
```

### Access and identity model

- **Pools are unlisted** — you need the code or link to reach one; there is no listing.
- **Password unlock** sets a tamper-proof cookie (ASP.NET Data Protection). Because originals live outside `wwwroot` and are streamed through access-checked endpoints, requesting an image or ZIP URL without the cookie returns a 404, not the bytes.
- **The admin link** carries a one-time capability key. On first use it is exchanged for a signed admin cookie and stripped from the URL, so the key doesn't linger in history or logs. Treat the link like a password; whoever holds it can manage the pool.
- **Uploader identity** is a random ID in a long-lived cookie. It drives the "your photos" badge, delete-your-own, and "download others'" — it is a convenience, not a security boundary.

### HTTP endpoints

| Route | Purpose |
|---|---|
| `GET /` | Home — join a pool by code, or create one |
| `GET/POST /Create` | Create a pool |
| `GET /p/{code}` | Gallery (redirects to unlock if locked) |
| `POST /p/{code}/unlock` | Verify password, set access cookie |
| `GET /p/{code}/admin` | Admin panel (via key or admin cookie) |
| `POST /api/p/{code}/photos` | Multi-file upload |
| `GET /api/photos/{id}/thumb` · `/display` · `/original` | Serve a rendition (access-checked) |
| `DELETE /api/photos/{id}` | Delete a photo (own, or as admin) |
| `GET /api/p/{code}/zip?mode=all\|others` | Streamed ZIP download |
| `GET /api/p/{code}/qr` | QR code PNG for the pool link |

## Project structure

```
src/GroupPhoto.Web/
├── Program.cs              # DI, middleware, EF init, upload limits
├── GroupPhotoOptions.cs    # configuration
├── Data/                   # EF Core context + Pool / Photo entities
├── Services/               # pools, photos, rendering, ZIP, access, cleanup…
├── Api/PhotoEndpoints.cs   # minimal-API upload/serve/zip/qr endpoints
├── Pages/                  # Razor Pages (home, create, gallery, unlock, admin)
└── wwwroot/                # site.css + gallery.js (no build step, no framework)
Dockerfile · docker-compose.yml
```

## Notes and limitations

- **Not for sensitive images.** Files are stored unencrypted on disk, so anyone with access to the server or its backups can read them. Don't upload anything private to a host you don't fully control. See the note at the top.
- **Not long-term storage.** There is no redundancy or automatic backup; back up the data directory if a pool matters, and don't rely on it as anyone's only copy.
- **Lightweight security.** Access rests on unguessable links and an optional shared password — there are no accounts, rate limiting, or audit logging. It's appropriate for casual event sharing, not for protecting confidential material.
- **The links are the credentials.** Anyone with the pool link (and password, if set) can view and upload; anyone with the admin link can manage. There is no email or account recovery.
- **EXIF (including GPS) is preserved** on originals, and anyone in the pool can download them. Worth mentioning to privacy-conscious guests.
- **Single instance, single filesystem.** This is deliberately simple software for an event, not a scalable photo platform. It expects one server and one data folder.

## Tech stack

ASP.NET Core Razor Pages (.NET 10), EF Core + SQLite, Magick.NET for all image rendering
including HEIC/HEIF (self-contained native — no system packages required), QRCoder, and a
vanilla JS/CSS front end, built as a multi-stage Docker image.

## License

[MIT](LICENSE)
