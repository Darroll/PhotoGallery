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

### Email alerts

Site settings has an **Email alerts** panel (relay host/port, sender, your address, which alerts). No extra services: messages are saved in an outbox table and a built-in background loop sends them through your SMTP relay, retrying with a growing pause (1 min up to 6 h) if the relay is down. Alerts: guest photos waiting for approval, a share link nearly (20% left) or fully used up, a gallery expiring within N days, and an optional Monday weekly summary. Each alert goes out once. **Send test email** checks the relay settings.

### Videos

In a gallery's settings, **Videos and links** takes YouTube links (watch, youtu.be, shorts, live). They show as a **Videos** grid above the photos so all recordings of an event sit in one place. Leave the title empty and the video's own title is used. Preview images are fetched once by the server and cached, so visitors do not contact YouTube until they press play; playback uses the privacy-enhanced youtube-nocookie player in a pop-up. Other links stay as buttons.

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

## Hardening checklist

Built in: PBKDF2 gallery passwords, constant-time admin check, rate limits (login, unlock, upload), slow-down after wrong admin passwords plus an email alert, per-visitor limit of one ZIP at a time, strict CSP, HSTS over HTTPS, 150 MP image cap, 60 s exiftool limit, and proxy headers (`X-Forwarded-*`, `CF-Connecting-IP`) believed only from `TRUSTED_PROXIES` (default: Docker's 172.16.0.0/12 and loopback).

Do on your side:
1. Remove the WebUI port mapping from the container (SWAG reaches it by name on `proxynet`) so nothing on the LAN can talk to it directly.
2. Protect `/admin` and `/api/admin` with Cloudflare Access (free) or a SWAG allow-list.
3. Keep the Cloudflare proxy on (not paused); restrict SWAG to Cloudflare's IP ranges; add a Cloudflare rate-limit rule for `/api/admin/login` and `/api/g/*/unlock`.
4. Use a long random `ADMIN_PASSWORD`.
5. Template extra parameters: `--security-opt no-new-privileges --cap-drop ALL --read-only --tmpfs /tmp`.
6. Rebuild the image regularly (runtime, libvips and exiftool fixes); Dependabot is configured for NuGet and Docker.

## Deploy on Unraid

1. Get this folder onto the server and build the image (the template uses a local image):
   ```bash
   docker build -t photogallery:latest /mnt/user/appdata/photogallery-src
   ```
2. Copy `unraid/my-photogallery.xml` to `/boot/config/plugins/dockerMan/templates-user/`, then Docker tab -> Add Container -> select **photogallery**.
3. Set the library path, `ADMIN_PASSWORD`, and put the container on the same custom network as SWAG (template assumes `proxynet`).
4. Copy `unraid/gallery.subdomain.conf` to `/mnt/user/appdata/swag/nginx/proxy-confs/`, add `gallery` to SWAG's `SUBDOMAINS`, create the DNS record, restart SWAG.
5. Open `https://gallery.yourdomain/admin`, create galleries from folders.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `ADMIN_USERNAME` | empty (password only) | Admin user name; when set, the sign-in form asks for it. Only settable here, not in the app |
| `ADMIN_PASSWORD` | random (logged) | Admin login |
| `ADMIN_PASSWORD_LOGIN` | `true` without SSO, `false` once SSO is configured | Set `true` to keep the password form alongside SSO, or `false` to turn it off |
| `OIDC_AUTHORITY` | empty (sign-in off) | Issuer URL of the OpenID Connect provider, e.g. `https://auth.example.com/realms/gallery` |
| `OIDC_CLIENT_ID` / `OIDC_CLIENT_SECRET` | empty | Client of the gallery in the provider |
| `OIDC_ADMIN_ROLE` | `gallery-admin` | Provider role or group that makes a user an admin (email must be verified) |
| `OIDC_ALLOW_HTTP` | `false` | `true` allows an `http://` provider, for local tests only |
| `PHOTO_LIBRARY` | `/photos` | Library root |
| `DATA_DIR` | `/data` | DB, cache, keys, guest uploads |
| `SIZES` | `1080,1920,2560,4096` | Offered long-edge sizes |
| `RESCAN_MINUTES` | `15` | Auto-rescan interval (0 = off) |
| `SITE_TITLE` | `Photos` | Initial site name; afterwards edit it in Admin -> Site settings |
| `MAX_UPLOAD_MB` | `100` | Per-file guest upload limit |
| `MAX_PENDING_PER_GALLERY` | `200` | Pending upload cap |
| `TRUST_PROXY` | `true` | Honour `X-Forwarded-*` |

## Sharing, search and details

**Link previews.** A shared gallery link shows its name and cover in chat apps and social feeds. Public and unlisted galleries reveal their name and a picture; a protected gallery shows only the site name, so its title and photos stay private. Expired galleries show only the site name.

**Subscriptions.** Visitors can subscribe on the home page to emails about new public galleries and new photos. Each subscriber confirms by a link first (double opt-in), and every email has an unsubscribe link. Unlisted and protected galleries are never announced. Subscriptions need the site address (Admin -> Settings -> Email alerts) and can be turned off there, where you also see how many people are confirmed.

**Details.** The ⓘ Details button in the viewer lists the metadata of every file behind a photo: the processed image and its RAW, if it has one. With exiftool installed on the server you get the full set (camera, lens, settings, dates); without it, the image library's own fields are shown. Where a photo was taken (GPS) is shown only to the admin.

**Captions and alt text.** Each photo can have a short caption and an alt text (a description for screen readers, which is also used as the image's alt attribute). Visitors who sign in can suggest changes from Details. Suggestions are reviewed in Admin -> Settings -> Caption suggestions, like a merge request: approving applies the text, rejecting drops it, and if the text has changed since the suggestion was made it is marked as a conflict and needs an explicit "Apply anyway". The admin's own edits apply straight away.

*Automatic captions* need a vision model you run yourself. Set `CAPTION_URL` to an Ollama server (for example `http://192.168.2.5:11434`) and optionally `CAPTION_MODEL` (default `llava`). The server writes captions for photos that have none, a few at a time in the background, and gives up on a photo after three tries. Photos are sent only to that address. Leave `CAPTION_URL` empty and nothing leaves the server. Run the model on a machine with enough memory: a vision model on the Unraid server itself can exhaust its RAM.

**Search and tags.** The search box on the home page (and `/search`) finds public photos by file name, caption, alt text, tags, gallery title, place, and date taken. Protected, unlisted and expired galleries are never searched.
Tags are labels you choose, about anything (Admin -> Settings -> Tags). Add them to a photo from its Edit sheet in the viewer, or to a whole gallery from its Tags field in gallery settings. Visitors see tags under photos and galleries (and on the home page), and can search by them. A gallery tag counts for every photo in that gallery, and the search page has a tag filter.

## Themes

Visitors can pick a theme from the **Theme** menu in the header. The choice is remembered in their own browser (no account needed) and applies to every page. Visitors who sign in with single sign-on also have it saved to their account, so it follows them to every device and browser: the account's theme wins over the browser's, and the first time someone signs in, the theme already chosen in that browser is saved to the account. (The password-only admin has no account, so the admin's choice stays in the browser.) Preferences are stored as JSON in the `Users` table (`UserPreferences`), so more can be added later. Themes change colors, fonts, corner rounding, shadows and photo-grid spacing, but never the layout. Photos in the full-screen viewer always sit on black.

| Theme | Look |
|---|---|
| Auto (device) | Light or dark, following the visitor's device setting |
| Light / Dark | The standard light and dark palettes |
| Midnight | Near-black, cool blue accent, rounded corners, soft shadows |
| Gallery White | Pure white, square corners, tight grid, black accent |
| Warm | Paper tones with a serif typeface |
| High contrast | Black and white with a yellow accent and strong borders |

In **Admin -> Settings -> Appearance** you set the default theme for new visitors, and can switch the menu off so everyone gets the default. To add a theme, add its `{ id, label }` to `ThemeSettings.All` and a matching `html[data-theme="id"]` block (all tokens) in `style.css`; a test fails if a theme is missing from the stylesheet or incomplete. Only system fonts are used, because the site's content security policy blocks remote fonts.

## License agreement

The gallery ships with a general-purpose photo license (personal, non-commercial use only; no resale, no advertising or stock use, no AI training, watermarks and credits must stay). It is an example, not legal advice, so have it reviewed for your country.

- Visitors see it at `/license` and through a footer link.
- By default they must tick an agree box in the download dialog before any download. Acceptance is recorded on the server (see below), and downloads are refused until the visitor has accepted the current wording, so the tick box cannot be skipped by calling the download links directly. Admins are not asked. Changing the wording asks everyone to accept again. Saving a copy of what is shown on screen is still possible, so watermarks (Admin -> Settings) remain the deterrent for reuse.
- Edit it in **Admin -> Settings -> License agreement**: turn it off, turn the required tick-box off, set the title, copyright holder, contact address and the full text. `{holder}`, `{site}`, `{year}` and `{contact}` are filled in automatically. **Reset text to default** restores the built-in wording.
- It is stored in `settings.json` in `DATA_DIR`, so it survives updates.
- **Records:** each accept is saved in the `LicenseAcceptances` table with the wording version and time. Signed-in people are recorded against their account; everyone is also tied to their browser's visitor cookie. IP addresses are not stored with acceptances. Admin -> Settings -> License agreement shows the log (who, when, and whether they accepted the current wording).
- **Enforcement:** single-photo downloads, RAW downloads and ZIPs return 403 with `licenseRequired` until the visitor has accepted the current wording. Admins are exempt.

## Versioning

The version shown at the bottom right of every page is `<Version>` in `src/PhotoGallery.csproj` plus the git commit it was built from (hover for the build time). Bump `<Version>` for a release; `release.sh` passes the commit and build time into the image.

## Development

```bash
cd src
PHOTO_LIBRARY=/path/to/photos DATA_DIR=./data ADMIN_PASSWORD=dev dotnet run
```

## Tests

```bash
dotnet test tests/PhotoGallery.Tests
```

The suite (163 tests, a few seconds) covers password hashing, configuration rules (including the SSO / admin password behaviour), the license settings and their placeholders, settings persistence, gallery rules, and the running app end to end: admin sign-in and sign-out, SSO mode, the license API, creating and scanning galleries, protected galleries and unlock, share links and download limits, per-gallery download switches, guest-upload moderation and the security headers. The integration tests start the real app against a throw-away library and data folder, so nothing on your machine is touched. GitHub Actions runs them (plus a syntax check of `app.js` and the shell scripts) on every push and pull request.

Add a test whenever you add or fix a feature. `AppFactory` in `tests/PhotoGallery.Tests` boots the app; pass extra environment-style settings to a subclass to test other configurations (see `SsoFactory`).

## Releasing

From the dev machine, on `main` with everything committed and pushed:

```bash
./deploy.sh
```

It runs the tests, checks that `main` matches GitHub, copies that commit to the Unraid server (`DEPLOY_HOST`, default `voltron`), builds the image there with `release.sh` and pushes it to the local registry. Then use "check for updates" / "apply update" on the container in Unraid. `SKIP_TESTS=1 ./deploy.sh` skips the test run.

The database schema is created with `EnsureCreated`. Future schema changes need EF migrations or a manual reset of `/data/photogallery.db` (galleries are re-creatable; originals are untouched).


## Single sign-on (container setup)

The gallery can sign visitors and the admin in through any OpenID Connect provider. SSO is off until `OIDC_AUTHORITY` and `OIDC_CLIENT_ID` are both set. Setting up the provider itself (realm, users, client) is not covered here.

You need these from your provider:

| What | Value |
|---|---|
| Issuer URL | e.g. `https://auth.example.com/realms/gallery` (must be reachable from the container) |
| Client ID and secret | A confidential client using the authorization code flow with PKCE (`S256`) |
| Redirect URI registered at the provider | `https://<gallery host>/signin-oidc` |
| Admin role or group | Name of the role or group whose members should be gallery admins (default `gallery-admin`) |

### Unraid

1. Open the **photogallery** container, Edit, and click **Show more settings** so the advanced variables appear.
2. Fill in:
   - `OIDC_AUTHORITY`: the issuer URL, with no trailing slash
   - `OIDC_CLIENT_ID`: the client id
   - `OIDC_CLIENT_SECRET`: the client secret
   - `OIDC_ADMIN_ROLE`: only if your admin role is not named `gallery-admin`
3. Leave `ADMIN_PASSWORD_LOGIN` empty. Click **Apply**.

### Docker run / compose

```yaml
environment:
  OIDC_AUTHORITY: https://auth.example.com/realms/gallery
  OIDC_CLIENT_ID: photogallery
  OIDC_CLIENT_SECRET: change-me
  OIDC_ADMIN_ROLE: gallery-admin   # optional
```

### Behind SWAG (nginx)

The SSO session cookie is large, so the proxy needs a bigger header buffer. `unraid/gallery.subdomain.conf` already sets `proxy_buffer_size 16k`; if you use your own proxy config, add it there too. The container must also trust the proxy's forwarded headers (`TRUST_PROXY=true`, the default), otherwise the redirect URI is built with `http://` and the provider rejects it.

### What changes once it is on

- The header shows **Sign in** / **Sign out**, and `/admin` offers **Sign in with SSO**.
- Users who have the admin role and a verified email become admins.
- The admin password form is hidden and `/api/admin/login` returns 404. Set `ADMIN_PASSWORD_LOGIN=true` to keep it as a fallback (for example while testing SSO, or if the provider is down).
- `OIDC_ALLOW_HTTP=true` permits an `http://` provider, for local testing only.

### Troubleshooting

- **"Sign-in failed" chip after returning from the provider**: check the container log. Common causes are a wrong client secret, a redirect URI that does not match exactly, or the container being unable to reach `OIDC_AUTHORITY`.
- **Signed in but not admin**: the user lacks the admin role or group, or their email is not verified. The admin page says you are signed in as a non-admin account.
- **Locked out**: set `ADMIN_PASSWORD_LOGIN=true` and `ADMIN_PASSWORD`, restart, sign in with the password.
