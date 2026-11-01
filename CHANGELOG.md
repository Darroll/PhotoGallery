# Changelog

Newest first. "Needs attention" marks anything an existing user must do when updating.

## Unreleased

- **Permission self-check.** The Overview now reports plainly when the app cannot read the photo library or cannot write its data folder, with the exact `chown -R 99:100 ...` command.
- **Reverse-proxy check.** The Overview warns when requests arrive with proxy headers from an address outside `TRUSTED_PROXIES` (the headers are ignored, so statistics and rate limits are wrong) and names the address to add.

- **Port-exposure check.** The Overview warns when a connection arrives straight from an internet address instead of through a proxy on your network, which means port 8080 is forwarded from your router. The README and Getting started now say near the top that a fresh install is private (no galleries, new galleries start Protected) and that the port is for your home network only.

- **Project housekeeping.** GitHub issue forms for bugs and feature requests, and a `SECURITY.md` describing private vulnerability reporting.

- **Copy diagnostics.** A button on the Overview that gathers version, environment, settings and recent warnings and errors into one block for bug reports, with secrets and email addresses removed.
- **Built-in help.** An admin **Help** menu (Getting started, Troubleshooting, report a problem) and a Help link on every amber or red Overview check that opens the matching Troubleshooting section.
- **New-version notice (off by default).** Settings -> Site and appearance -> New versions. When on, the server asks GitHub once a day for the newest release number (nothing about your site is sent) and the Overview turns amber when you are behind. The version in the footer links to this changelog for admins.
- **Proxy and tunnel guide** ([docs/PROXY.md](docs/PROXY.md)): step-by-step settings for SWAG, Nginx Proxy Manager and Cloudflare Tunnel, which address goes in `TRUSTED_PROXIES`, and how to check it worked.
- **Privacy tools.** Admin -> Settings -> Privacy: an editable privacy notice (shown at `/privacy`, linked from the footer), and a lookup by email address that exports everything held about a person as JSON or deletes it (comments, comment log, likes, sign-in record, subscription, group and gallery invitations, queued mail, statistics tied to their browser). A comment block is kept unless you choose to remove it. The delete-gallery confirmation now says its likes and comments go with it.
- **Setup guide.** A first-run guide (also under Help -> Setup guide) that names the site, checks the photo folder is readable, turns a folder into the first gallery (Private, Anyone with the link, or Public) and tests the public address. Every step can be skipped. It opens once, the first time an admin lands on the Overview of an install with no galleries.
- **Getting-started checklist** on the Overview: make a gallery, share it, have a backup (essentials), plus the optional address, email alerts and visitor sign-in. It goes away when the essentials are done, or when you hide it.
- **"Public address" check** on the Overview. It tries the saved site address from the server and says in plain words what is wrong: name not found, nothing listening, no answer, bad certificate, the proxy cannot reach the app, another site answering, a home-only address, or plain http. `/healthz` now sends an `X-PhotoGallery` header so the check can tell this app from another site.
- **Screenshots** in the README (in `docs/screenshots/`).

## 1.1.0

- **First-run setup.** With no `ADMIN_PASSWORD`, the first visit to `/admin` asks you to choose a password instead of printing a random one in the log on every start. It is stored hashed in `/data/admin-password`, can be changed under Settings -> Site and appearance, and is reset by deleting that file. From outside the home network the page needs the setup code printed in the container log. **Needs attention:** if you relied on the random password, open `/admin` once after updating and choose a password. If `ADMIN_PASSWORD` is set, nothing changes.

## 1.0.0

- Published to Unraid Community Apps. The public image is `ghcr.io/darroll/photogallery`. Pushes to `main` publish only `:edge`; `:latest` and the version tags (`:1.0.0`, `:1.0`) move only on a tagged release, and only after the tests pass.
- New documentation: [Getting started](docs/GETTING-STARTED.md) and [Troubleshooting](docs/TROUBLESHOOTING.md).
- Template changes: it now pulls from GitHub's container registry, uses the default bridge network, and `ADMIN_PASSWORD` is optional. **Needs attention:** if you installed from the author's earlier private template, edit the container and set Repository to `ghcr.io/darroll/photogallery:latest` to follow public updates.
- Earlier work (galleries, downloads, RAW, ZIP, watermarks, share links, guest uploads, likes, comments, SSO, email alerts, backups) is described in the [README](README.md).
