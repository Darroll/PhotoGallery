# Getting started

This takes about ten minutes and needs nothing but the container. No reverse proxy, single sign-on or email is required to begin; add them later.

## 1. Install

Install **PhotoGallery** from the Apps tab (or see "Deploy on Unraid" in the [README](../README.md)). Two fields matter:

| Field | What to put |
|---|---|
| **Photo library** | The folder that *contains* your photo folders, for example `/mnt/user/Photos`. It is mounted read-only; PhotoGallery never changes, moves or deletes your photos. |
| **ADMIN_PASSWORD** | Optional. Leave it empty and the app asks you to choose a password the first time you open `/admin` (step 2). Fill it in only if you prefer to keep the password in the container settings; then it always wins and cannot be changed in the app. |

Leave **App data** at `/mnt/user/appdata/photogallery`. It holds the database, cached thumbnails, guest uploads and encryption keys. Include it in your appdata backup.

### Permissions

The container runs as the Unraid user `nobody` (uid 99, group `users`). It must be able to **read** your photo share and **read and write** the app data folder. On a normal Unraid share this works as is. If galleries show no photos or the Overview page reports a problem, run this in the Unraid terminal:

```bash
chown -R 99:100 /mnt/user/appdata/photogallery
```

## 2. Sign in

Open `http://YOUR-SERVER-IP:8080/admin` from a computer on your home network.

- **First time, no `ADMIN_PASSWORD` set:** a **Welcome** page asks you to choose the admin password (at least 10 characters, typed twice). You are signed in straight away. From outside your home network the page also asks for a **setup code**; it is printed in the container log (Docker tab -> the container's icon -> **Logs**), which keeps a stranger from claiming a server you exposed to the internet.
- **Later:** enter the password (and the user name only if you set `ADMIN_USERNAME`). Change it under **Settings -> Site and appearance**. The first page is the **Overview**, which shows green, amber or red checks. Fix any red item first; each one links to where you fix it.

## 3. Create your first gallery

1. **Galleries -> Create gallery** and pick a folder under your library. One folder is one gallery; sub-folders are included.
2. Name it and save. The first scan runs at once; thumbnails are built in the background, so a large folder looks plain for a few minutes (see [Troubleshooting](TROUBLESHOOTING.md)).
3. Open the gallery's **Settings**. The one choice to make now is **Access**:
   - **Public**: listed on the home page for everybody.
   - **Unlisted**: not listed, anyone with the address can open it.
   - **Protected**: needs a password and/or a share link.
   - **Specific people**: only invited email addresses, after single sign-on (set up [single sign-on](../README.md#single-sign-on) first).
4. Press **Save**, then view the gallery to see what a visitor sees.

## 4. Share it

- **Public or Unlisted:** copy the gallery address and send it.
- **Protected:** in the gallery's settings create a **share link** (optionally with an expiry date and a download limit) or set a password. Share links can be revoked at any time.
- Visitors can watch a slideshow, like photos and download at the sizes you allowed. You decide per gallery whether originals, RAW files and ZIPs are offered.

## What your folders should look like

```
/mnt/user/Photos            <- the "Photo library" you mounted
    2025-Wedding/           <- one gallery
        IMG_0001.jpg
        IMG_0001.CR3        <- RAW file, paired with the JPEG by name
    Holiday/                <- another gallery
```

Supported pictures: JPG, PNG, TIFF, WebP, HEIC, AVIF. Supported RAW files: CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2, PEF, SRW. Video files are not supported (add YouTube links in the gallery settings instead).

## 5. Let people outside your home reach it

Only do this once the steps above work on your own network.

- Put the container behind a **reverse proxy with HTTPS**: SWAG (a ready config is in `unraid/`), Nginx Proxy Manager, or a Cloudflare Tunnel. **Do not forward port 8080 from your router.**
- The proxy must (a) pass the `X-Forwarded-For`, `X-Forwarded-Proto` and `Host` headers, (b) allow uploads of at least `MAX_UPLOAD_MB` (default 100 MB; a Cloudflare free plan caps at 100 MB), and (c) not buffer responses, so large ZIP downloads stream.
- Set **`TRUSTED_PROXIES`** to the address range your proxy connects from. The default only trusts Docker's own networks (`172.16.0.0/12`). If your proxy runs on another machine or has a LAN address such as `192.168.x.x`, add it; otherwise all visitors appear to come from the proxy and rate limits and statistics are wrong.
- Protect `/admin` as well (Cloudflare Access or a proxy allow-list) and use a long password.

## 6. Optional next steps

All of these are under **Admin -> Settings** and can wait:

| Want | Where |
|---|---|
| Your site name, theme, watermark | Site and appearance, Watermark |
| Email alerts (new uploads, expiring links) | Email alerts (needs an SMTP relay; Gmail needs an app password) |
| Let people sign in, comment, get invited | Sign-in (SSO); needs Keycloak or another OpenID Connect provider |
| A copy of your database on another disk | `BACKUP_COPY_DIR` on the container, and Statistics -> Database backups |
| Automatic captions | `CAPTION_URL` pointing at an Ollama server |

## Updating

In the Docker tab press **Check for updates**, then **apply update**. Your data lives in the app data folder and is kept. The database is backed up and upgraded automatically at start. Read the [changelog](../CHANGELOG.md) first for anything that needs your attention.

## Removing it

Stop and remove the container. Your photos are untouched. Delete `/mnt/user/appdata/photogallery` only if you also want to discard galleries, likes, comments and settings.
