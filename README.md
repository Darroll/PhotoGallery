# PhotoGallery

Self-hosted photo galleries for Unraid, written in ASP.NET Core 9 (SQLite + libvips). No viewer accounts; one admin.

## Features

| Need | How it works |
|---|---|
| Multiple galleries | Each gallery points at a folder under the photo library (read-only mount). Rescan any time. |
| Public / private | **Public** (listed on home page), **Unlisted** (anyone with the URL), **Protected** (password and/or share link). |
| Slideshow | Fullscreen lightbox with play/pause, 3-12 s interval, keyboard (space, arrows, F, Esc) and swipe. |
| Resolutions, no quality loss | Downloads at configured long-edge sizes (`SIZES`) as **lossless PNG, lossless WebP or LZW TIFF**, or the **byte-identical original**. On-screen viewing uses WebP q88. |
| RAW download | RAW files are paired with the processed image by name (per-gallery: same folder, any folder, or `IMG_1234-Edit.png` style prefix), or linked by hand in the admin page. RAW-only frames are hidden from viewers until they click *Show RAW-only photos*. Per-gallery switch `Allow RAW download`. RAW-only files are supported when `exiftool` is present (it is in the Docker image). |
| ZIP | Whole gallery as ZIP (original or chosen size, optional RAW folder), streamed. |
| Watermarks | Configured in Admin -> Settings: none, text or transparent PNG, with position, opacity, size and live preview. Per gallery: on screen and/or on downloads. |
| Expiring links, download limits | Share links with expiry date and max downloads; revocable. A ZIP counts as one download. |
| Guest uploads | Per gallery. Always moderated: nothing shows until you approve it. Stored in `/data`, never in your library. |

### Admin pages

- `/admin`: create galleries from library folders; list of galleries with cover, status chips and a Settings link.
- `/admin/galleries/<id>`: per-gallery settings in collapsible sections (General, Access & sharing, Downloads, Watermark & uploads, RAW files - collapsed by default) with a single Save button and an unsaved-changes warning.
- Per-gallery options include photo order (capture time / name / date added, reversible), event date and location, auto-expire date, offered download sizes and formats, and the curated-only default.
- `/admin/settings`: site-wide watermark (none / text / PNG) with live preview, the order of galleries on the home page (event date, name, date created or manual with up/down arrows on the admin list), and the thumbnail cache.

### Photo pages and browser history

Each photo has its own address, `/g/<gallery>/p/<id>`, which can be shared (protected galleries still ask for the password or link first). Opening the viewer adds one history entry; moving between photos or running the slideshow only replaces the address. The mouse Back button, Esc and the Close button return to the grid at the same scroll position, and Forward reopens the photo.

### Visitor statistics

`/admin/stats` (and a Statistics button on every gallery's settings page) shows unique IPs, gallery and photo views, downloads by size/format, ZIPs, share-link opens, wrong passwords, guest uploads, top galleries and photos, countries and a recent-activity table with each visitor's full IP address and browser. Bots and crawlers (detected from the user agent) are kept on a separate tab. **By visitor** groups the log by a visitor id cookie (anonymous, per browser, issued only to non-bots; falls back to IP + browser for older records), by IP + browser string, or by IP alone, so people who share an IP address (a household) can be told apart, and the visitor page lists others seen on the same IP. The cookie can be switched off in Site settings. Within the visitor view, click an IP to see that visitor's visits (new visit after 30 minutes idle), galleries, the photos they looked at or downloaded (with thumbnails and sizes), their browsers and a full timeline. Your own admin visits are not recorded. Retention (default 180 days) and a delete-all button are in Site settings.

The visitor IP comes from `CF-Connecting-IP` when Cloudflare proxies the site, otherwise from the address SWAG forwards. Country is only known while Cloudflare proxies the site. IP addresses are personal data: keep only what you need and tell visitors if your local rules require it.

### Page titles

Every page has its own browser tab title: the site name (Admin -> Site settings) on the home page, the gallery name on a gallery (gallery names must be unique), `<photo name> - <gallery name>` on a photo page, and descriptive titles for the password, closed, expired-link, not-found and admin pages.

### Choosing photos, favorites and the mobile view

- **Select** on a gallery lets viewers tick photos and download just those as a ZIP (size, format and RAW chosen in a download sheet). The same sheet downloads a single photo, favorites, or the whole gallery.
- **Favorites**: a heart on every photo. Favorites are stored per anonymous browser id; viewers can add their name, and the photographer reviews every list in the gallery settings (Favorites section: per-visitor lists, most favorited photos, copy file names or RAW names for Lightroom, download originals + RAW as a ZIP).
- **Footage links**: each gallery can show buttons under its title (for example a YouTube link to game footage). Only http/https links are kept.
- **Mobile**: responsive layout on every page, a bottom-sheet download dialog, and a full-screen viewer with swipe, double-tap and pinch zoom, swipe-down to close and a lighter 1280px image (a sharper one loads when zoomed).

### Thumbnails

Grid thumbnails and viewing images are built in the background after every scan (and shortly after start-up), then cached in `/data/cache`. A gallery's settings page has **Generate missing** (builds only what is absent) and **Rebuild all**; Site settings shows the cache size, can **clean up stale files** (deleted or replaced photos, old watermark variants) and **generate missing** / **rebuild all** for every gallery.

### Design notes you should know

- **"Lossless" and resizing:** shrinking an image always discards pixels. The sized exports add *no extra compression loss* on top of the downscale. For zero change, use *Original*.
- **Watermark on downloads blocks originals and RAW** (they would bypass the watermark). Only sized exports are offered.
- **RAW-only files** use the preview JPEG embedded in the RAW for viewing and sized exports. Sized exports are not a RAW develop.
- Your originals are never modified or deleted. "Delete gallery" only removes the gallery record and cached files.
- Download limits and expiry apply to people arriving through a share link. Password users and public galleries are not limited.

## Security model

- Admin: one password (`ADMIN_PASSWORD`), HttpOnly SameSite=Strict cookie, rate-limited login, custom header required on all write calls.
- Viewers: gallery access is an encrypted, expiring cookie (ASP.NET Data Protection, keys persisted in `/data/keys`). Changing a gallery's password or visibility invalidates existing cookies.
- Protected photos are never served without a valid grant (images, downloads and ZIPs all check).
- Upload: extension allow-list, size limit, content verified by libvips before acceptance, per-IP rate limit, pending cap.
- Strict CSP, `nosniff`, no framing. Run it only behind SWAG/HTTPS and do not publish the container port to the internet.

## Deploy on Unraid

1. Get this folder onto the server and build the image (the template uses a local image):
   ```bash
   docker build -t photogallery:latest /mnt/user/appdata/photogallery-src
   ```
2. Copy `unraid/my-photogallery.xml` to `/boot/config/plugins/dockerMan/templates-user/`, then Docker tab -> Add Container -> select **photogallery**.
3. Set the library path, `ADMIN_PASSWORD`, and put the container on the same custom network as SWAG (template assumes `proxynet`).
4. Copy `unraid/photos.subdomain.conf` to `/mnt/user/appdata/swag/nginx/proxy-confs/`, add `photos` to SWAG's `SUBDOMAINS`, create the DNS record, restart SWAG.
5. Open `https://photos.yourdomain/#/admin`, create galleries from folders.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `ADMIN_PASSWORD` | random (logged) | Admin login |
| `PHOTO_LIBRARY` | `/photos` | Library root |
| `DATA_DIR` | `/data` | DB, cache, keys, guest uploads |
| `SIZES` | `1080,1920,2560,4096` | Offered long-edge sizes |
| `RESCAN_MINUTES` | `15` | Auto-rescan interval (0 = off) |
| `SITE_TITLE` | `Photos` | Initial site name; afterwards edit it in Admin -> Site settings |
| `MAX_UPLOAD_MB` | `100` | Per-file guest upload limit |
| `MAX_PENDING_PER_GALLERY` | `200` | Pending upload cap |
| `TRUST_PROXY` | `true` | Honour `X-Forwarded-*` |

## Versioning

The version shown at the bottom right of every page is `<Version>` in `src/PhotoGallery.csproj` plus the git commit it was built from (hover for the build time). Bump `<Version>` for a release; `release.sh` passes the commit and build time into the image.

## Development

```bash
cd src
PHOTO_LIBRARY=/path/to/photos DATA_DIR=./data ADMIN_PASSWORD=dev dotnet run
```

The database schema is created with `EnsureCreated`. Future schema changes need EF migrations or a manual reset of `/data/photogallery.db` (galleries are re-creatable; originals are untouched).
