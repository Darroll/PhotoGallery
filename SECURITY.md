# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Report it privately through GitHub: <https://github.com/Darroll/PhotoGallery/security/advisories/new> (the **Report a vulnerability** button on the repository's Security tab). Include the version (shown on Admin -> Overview), what you found, and steps to reproduce it. Do not include real passwords or other people's private photos.

You can expect an acknowledgement within a few days. PhotoGallery is maintained by one person in spare time, so a fix may take longer; I will tell you what I plan to do and credit you in the changelog if you wish.

## Supported versions

Only the latest release (the `:latest` image) gets security fixes. Update from the Unraid Docker tab ("check for updates", then "apply update").

## What counts

For example: reading a private or protected gallery without access, getting into `/admin`, uploading or reading files outside the intended folders, making the server fetch addresses on your private network, or leaking visitors' email addresses.

Not a vulnerability: running the app with its port forwarded straight to the internet (the Overview warns about this), or a weak admin password you chose.

## Hardening tips

- Keep port 8080 on your home network; share through a reverse proxy with HTTPS or a Cloudflare Tunnel.
- Set `TRUSTED_PROXIES` to your proxy's address, protect `/admin` as well, and use a long admin password.
- Keep the container updated and include the app data folder in your backups.

See [Getting started](docs/GETTING-STARTED.md), step 5, and the security notes in the [README](README.md).
