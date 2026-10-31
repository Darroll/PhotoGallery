# Troubleshooting

Start with **Admin -> Overview**: it checks most of the problems below and links to the fix. The container log (Docker tab -> **Logs**) is the second place to look.

## I cannot sign in to /admin

- Forgot the password you chose in the app: stop the container, delete the file `admin-password` in the app data folder (`/mnt/user/appdata/photogallery`), start it and open `/admin` to choose a new one. If `ADMIN_PASSWORD` is set in the container, that value is the password.
- You set `ADMIN_USERNAME`: the form then also asks for the user name.
- Too many wrong tries: wait a few minutes; the sign-in slows down on purpose.
- Single sign-on is on and the password form disappeared: set `ADMIN_PASSWORD_LOGIN=true` on the container to bring it back.
- Sign-in works on `http://IP:8080` but not through your proxy: the proxy must pass `X-Forwarded-Proto` and `Host`, and the proxy's address must be in `TRUSTED_PROXIES`.

## The first-visit page asks for a "setup code"

You opened `/admin` from outside your home network (or through a proxy). Open the container log (Docker tab -> **Logs**) and copy the code from the line "No admin password yet". The code changes each time the container starts. Opening `/admin` from a computer on your home network, directly by `http://SERVER-IP:8080/admin`, needs no code.

## The gallery is empty or photos are missing

- Is the folder inside the **Photo library** you mounted? The container only sees what you mounted at `/photos`.
- File types: JPG, PNG, TIFF, WebP, HEIC, AVIF. RAW-only frames are hidden until a visitor chooses "Show RAW-only photos". Video files are not scanned.
- Permissions: the container user (`nobody`, 99:100) must be able to read the folders. Check the photo library line on the Overview.
- Press **Rescan** on the gallery; new files are otherwise noticed every `RESCAN_MINUTES` (15 by default).

## Photos show grey boxes or load slowly

Thumbnails are built in the background after the first scan. A large library (thousands of photos, big RAW files) takes a while, mostly on the first run. Watch progress under "Thumbnails" on the Overview. **Settings -> Thumbnail cache -> Generate missing** builds them at once. The cache lives in `/data/cache`; make sure the app data share has free space (the Overview warns when it runs low).

## "Permission denied", or the backup or uploads check is red

The app data folder must be writable by uid 99. In the Unraid terminal:

```bash
chown -R 99:100 /mnt/user/appdata/photogallery
```

## The container will not start

- **Port 8080 already in use:** change the host side of the WebUI port in the container settings.
- **Errors after changing Extra Parameters:** the container expects `--read-only --tmpfs /tmp` and writes only to `/data`. Keep both, or remove both.
- Read the log; the last lines say why.

## Large downloads or uploads fail behind a proxy

- Uploads: raise the proxy's body-size limit above `MAX_UPLOAD_MB` (nginx: `client_max_body_size`). Cloudflare's free plan caps requests at 100 MB.
- ZIPs and big exports stop part-way: turn off response buffering and raise read timeouts (nginx: `proxy_buffering off; proxy_read_timeout 900s;`).

## Every visitor shows the same IP, or rate limits hit everybody

The proxy's address is not in `TRUSTED_PROXIES`, so its forwarded headers are ignored. Add the proxy's network, comma separated, for example `172.16.0.0/12,192.168.1.10/32`.

## Emails are not sent

Open **Settings -> Email alerts** and press **Send test email**; it explains the failure in plain words. Gmail needs an app password, and only STARTTLS on port 587 is supported (not 465).

## Sign-in (SSO) fails

Follow **Settings -> Sign-in (SSO)**: its connection check and **test sign-in** button name the problem. The redirect address registered in the provider must exactly match the address in your browser, including `https://`. See the [README](../README.md#single-sign-on) for more.

## An update went wrong, or I want to go back

The database is backed up before any upgrade (`photogallery.db.bak-<time>` in the app data folder) and on a schedule (`/data/backup`, listed under **Statistics -> Database backups** with a Restore button). To go back to an older image, restore a backup from before the upgrade first, because an older version cannot open a newer database.

## Still stuck

Ask in the [Unraid forum thread](https://forums.unraid.net/topic/201119-support-darroll-photogallery/) or open a [GitHub issue](https://github.com/Darroll/PhotoGallery/issues). Include your Unraid version, the app version (shown in the app header), the container log with passwords removed, and what you expected to happen.
