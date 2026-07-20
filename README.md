<div align="center">

# 📸 GroupPhoto

**Collect everyone's photos from a shared event into one pool — no accounts, no app.**

Share a link (or QR code), let people open it on their phone, type their name, and upload.
Perfect for weddings, festivals, and holiday trips where the best photos are scattered
across a dozen phones.

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

</div>

---

## Why?

After a wedding or a weekend away, everyone has photos and no one has *all* of them. Group
chats compress them to mush, shared-album apps need accounts, and cloud drives are fiddly to
hand out. GroupPhoto is the boring, self-hosted answer: one page, one link, everybody's
full-resolution photos in one place.

## How it works

**For the organizer**

1. Create a pool, give it a name, and (optionally) set a password and an auto-delete date.
2. You get a share link + QR code to hand out, and a private admin link to keep.

**For everyone else**

1. Open the link (or scan the QR code) — no sign-up.
2. Type your name and drag in photos.
3. Browse the gallery and grab everyone else's photos as a ZIP.

## Features

- **🔗 Shareable pools** — one gallery per event, reachable by an 8-character code, a link, or a QR code. Pools are unlisted: no directory, no browsing other people's events.
- **🙈 No accounts** — uploaders just type a name. A browser cookie quietly remembers who they are, so their photos get a "you" badge and they can delete their own.
- **🔒 Optional passwords, done properly** — enter the password once per device. After that a signed cookie unlocks everything. Photo files live outside the web root and every image/download re-checks that cookie, so a leaked image URL is useless without the password.
- **🖼️ Fast, sharp gallery** — a WebP thumbnail grid, plus a full-screen lightbox backed by a ~1600px web-safe proxy — crisp to view, without pushing a 50 MB original down the wire. Filter by uploader; photos sort by capture time (EXIF).
- **📱 iPhone HEIC/HEIF** — decoded server-side, so they get real thumbnails and previews in *every* browser, not just Safari.
- **⬇️ Flexible downloads** — one photo, the whole pool as a streamed ZIP, or **"download others'"** — everything except your own uploads.
- **🧑‍✈️ Private admin link** — the creator can rename the pool, change or remove the password, adjust expiry, delete individual photos, or nuke the whole pool.
- **⏳ Auto-expiry** — optionally have a pool delete itself N days after the event.
- **🧹 Deduplication** — the same photo uploaded twice is stored once (SHA-256).
- **💾 Dead-simple storage** — files on disk + a SQLite database. Back up one folder and you've backed up everything.

## Quick start

With Docker — the easiest way to run it:

```bash
docker compose up -d --build
# open http://localhost:8080
```

Everything (photos, database, cookie-signing keys) is persisted in the `groupphoto-data`
volume mounted at `/data`. Back that up and you're safe.

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
| `DataPath` | `/data` (Docker) · `data` (local) | Root folder for the DB, photos, and keys |
| `MaxFileSizeMb` | `50` | Per-file upload limit |
| `ThumbnailSize` | `480` | Longest edge of gallery thumbnails (px) |
| `DisplaySize` | `1600` | Longest edge of the full-screen lightbox proxy (px) |
| `DefaultExpiryDays` | `0` | Expiry pre-selected on the create form (`0` = never) |
| `CookieLifetimeDays` | `90` | How long unlock/identity/admin cookies last |
| `PublicBaseUrl` | *(derived from request)* | Public URL used in share links & QR codes |

### Behind a reverse proxy

The app honours `X-Forwarded-Proto` / `X-Forwarded-For`, so HTTPS termination in
Caddy / nginx / Traefik works out of the box. Two things to set:

- `GroupPhoto__PublicBaseUrl` — your public address, so QR codes and share links point to the right place.
- Your proxy's request-body limit — at least `MaxFileSizeMb` (e.g. `client_max_body_size 50m;` in nginx).

## Under the hood

### Three renditions per photo

Every upload is decoded once (Magick.NET) and produces three files, so each context gets a
right-sized image:

| Rendition | Size | Format | Used for |
|---|---|---|---|
| Thumbnail | ~480px | WebP | Gallery grid |
| Display proxy | ~1600px | WebP | Full-screen lightbox |
| Original | untouched | as uploaded | Downloads & ZIPs |

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

### Access & identity model

- **Pools are unlisted** — you need the code or link to find one; there's no listing.
- **Password unlock** sets a tamper-proof cookie (ASP.NET Data Protection). Because originals live outside `wwwroot` and are streamed through access-checked endpoints, copying an image or ZIP URL gets you a 404 without the cookie — not the bytes.
- **The admin link** carries a one-time capability key. On first use it's exchanged for a signed admin cookie and stripped from the URL, so the key never lingers in history or logs. Treat the link like a password; whoever holds it can manage the pool.
- **Uploader identity** is a random ID in a long-lived cookie. It powers "your photos" badges, delete-your-own, and "download others'" — it's a convenience, **not** a security boundary.

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

## Notes & limitations

- **EXIF (including GPS) is preserved** on originals, and anyone in the pool can download them. Worth mentioning to privacy-conscious guests.
- **Single instance, single filesystem.** This is deliberately simple software for an event — not a scalable photo platform. It expects one server and one data folder.
- **Guard your links.** Anyone with the pool link (and password, if set) can view and upload; anyone with the admin link can manage. There's no e-mail recovery — the links *are* the credentials.

## Tech stack

ASP.NET Core Razor Pages (.NET 10) · EF Core + SQLite · Magick.NET for all image rendering
including HEIC/HEIF (self-contained native — no system packages required) · QRCoder ·
vanilla JS/CSS front end · multi-stage Docker build.

## License

[MIT](LICENSE)
