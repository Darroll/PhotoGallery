# Changelog

Newest first. "Needs attention" marks anything an existing user must do when updating.

## 2.1.0 (not released yet)

- **Automatic player tagging.** A vision model reads the jersey numbers in a gallery's photos and suggests a tag for each player on your roster. Use your own Ollama server, Claude or OpenAI (with an API key), in the order you choose; a later one is tried only when an earlier one fails, and only if it is set up. Add teams with their jersey colours and rosters under **Teams**, choose a gallery's team and start a check under **Players** in the gallery's settings, then approve or reject what it found. Only players in your team's colours are read, because the other team can wear the same numbers. Checks run when you press a button, or by themselves for new photos if you switch on **Check new photos automatically** (in Settings, or per gallery, where each gallery can follow the main setting or override it). Nothing is tagged without your approval unless you set a confidence for that. You can watch a check as it runs (the photo being read, which AI is reading it, the latest results), stop it, and see every gallery's checks in one Activity list. A team can have several jersey colour sets (Home, Away, Third) and each gallery picks the one worn, or one per folder in a tournament gallery. Numbers read on players in your colours that are not on the roster are listed so you can add the player in one click, which turns those photos into suggestions without checking them again. Numbers are matched exactly as written (0 and 00 are different players). When the model says part of a number may be hidden, the suggestion is marked "maybe" and never approved automatically; a number written with a hidden digit ("1?") offers each roster number it fits for you to pick one. Each photo can be fixed by hand: tap a suggestion's pencil, or a photo, then tap the right roster number, or "Not a player"; the wrong reading does not come back. Before adding a new number to the roster you can untick the photos that are not that player. Numbers not on the roster are listed in number order. Each provider has a timeout per photo (default 5 minutes, 0 for no limit). See *Automatic player tagging* in the README.
- **Player filter.** A gallery whose photos carry player tags shows a row of player chips above the photos (`#10`, or the player's name when the team uses names in tags). Tap one to see only that player's photos, and **Download all** to get them as one ZIP or, on a phone, saved to the photo app. The choice is remembered in the browser for that gallery. Downloads follow **Curated only**: while it is on, the unedited RAW frames are left out of a player's, a section's or the whole gallery's download, and each edited photo still brings its own RAW file when *Include the RAW files* is ticked. Works in every visibility, including unlisted and password-protected galleries. The database does not change for this.
- Updating adds four tables and ten columns to the database; a copy of the database is saved first.

## 2.0.0

- **Save to phone.** On a phone, the download sheet now has a **Save to phone** button. It puts the photos (one, the ticked ones, favorites, a game's section or the whole gallery) straight into the phone's photo app through the share sheet, 15 at a time, instead of a ZIP that phones handle badly. Originals are sent untouched when they are JPEG, HEIC or PNG and the gallery offers originals; otherwise a high-quality JPEG at the largest offered size (up to 4096 px, watermarked when downloads are). A save counts as one download on a share link, like a ZIP. The database does not change.
- **Runs on .NET 11.** The app and its container image move from .NET 9, which Microsoft stopped supporting on 10 November 2026, to .NET 11, supported until November 2028. The container image is now based on Ubuntu instead of Debian. Nothing changes in how the app looks or works, and the database does not change. Updating downloads a whole new image once, so it takes a little longer than usual.
- **QR codes** come from QRCoder 1.8 (was 1.6).
- The automated checks now build the container image and make sure the app starts inside it.

## 1.7.0

- **Messages from visitors.** Turn on **Let visitors send me a message** in a gallery's settings and the gallery gets a **Message** button. Visitors write a few words, with an optional name and a way to reply; no account is needed. You read, mark and delete them under **Messages** in the admin menu, and the optional email alert tells you when new ones arrive (**Email alerts** -> *Visitors have sent you messages*). A hidden trap field and a limit of 10 messages a minute per address keep spam out.
- **Subscriber emails as a daily or weekly digest.** Subscribers no longer get one email for every new gallery or batch of photos. By default they get **one email a day** with everything new, and none on days with nothing new; choose **once a week** or **straight away** under **Email alerts** -> *Subscribers get new galleries and photos*. **Needs attention:** if you relied on the old immediate emails, choose *straight away* there.
- **Library and schedule settings in the app.** New page **Library and schedule** (site settings): how often to look for new photos, how often to back up the database and how many daily and weekly backups to keep. A value set by a variable in the container (for example `RESCAN_MINUTES`) wins and shows as locked with the variable's name. Changes apply at once, without restarting. The page also shows which folder the photos are read from.
- Updating adds a table and a column to the database; a copy of the database is saved first.

## 1.6.0

- **Share button.** Every gallery has a **Share** button: the link (with Copy link), a QR code to show or print, a message ready to paste, and for a gallery with sections a choice of sharing the whole gallery or just one game, which opens straight on that tab. The link uses the address set in Setup when there is one. The QR code is only given to people who can open the gallery.
- **Sections show up in search.** Searching for a game's title (for example "Cooper City") finds that section as its own result, with a picture from it, and opens the gallery on that tab.
- **Scan time.** The rescan message and the log now say how long a scan took, so you can tell whether a big library needs patience.
- **Upgrade test.** An automated test now upgrades a database from the very first release to the current one and checks that galleries, photos, users, tags and likes are all kept and that a backup copy is made.
- Adds one library (QRCoder) to the image; the database does not change.

## 1.5.0

- **Photos are ordered by when they were taken, from the RAW file.** Every photo that has a RAW partner, the edited export as well as the RAW-only frame, now takes its shooting time from the RAW file's own metadata, including the hundredths of a second, so the frames of a burst keep their order and an edited PNG sits where it was shot instead of where it was saved. Photos without a RAW file keep the time in their own metadata, or the file date. This needs ExifTool, which the container already includes.
- **Needs attention:** after updating, press **Rescan folder now** on each gallery (or wait for the automatic rescan). The first scan reads the shooting time of every RAW file once (about 40 seconds for 2,500 photos) and says "read the shooting time of N photos from their RAW files". Later scans only read new files. If the order still looks wrong, check **Photo order** in the gallery settings is **Capture time**.
- Updating adds one column to the database; a copy of the database is saved first.

## 1.4.1

- **Bolder section tabs**, with an accent underline and a clearly highlighted open tab.
- **Counts on the tabs.** While the Curated filter is on, each tab shows curated/all (for example 57/305: 57 edited photos of 305 frames); with the filter off, or when a section has no RAW-only frames, just the number of photos. With Favorites showing, the tab shows its favorites. Hover a tab to see what the number means.

## 1.4.0

- **Sections are tabs now.** A gallery split into sections shows one tab per section (with its photo count) and only the open tab's photos on the page, so a tournament with thousands of frames opens fast. The open tab is in the address (`/g/my-gallery#s=Game_2`), so a link can open a given game, and a photo's own link opens the tab it is in. Slideshow, Select all and the viewer work on the open tab; the ZIP button in the tab downloads just that section.
- **Unedited RAW frames in a section.** A tab whose folder holds only RAW files (no edited copy next to them) used to look empty because the Curated filter hides those. It now says how many RAW frames are hidden and has a **Show the N RAW frames** button. To show them for everyone by default, untick **Curated filter** under RAW files in the gallery settings.
- **A cover that is not one of the photos.** Gallery settings -> Cover image has a new choice under the cover picker: **Also one of the photos** (as before), **Only the cover**, or **Only the cover, shown as a banner at the top**. A cover-only picture (a tournament logo, say) stays out of the photo grid, the slideshow, the photo counts, search and the ZIP downloads, but is still the picture on the home page card. Choosing a different cover puts the old one back among the photos.
- **Events that last several days.** Event date takes a first and a last day (the weekend of a tournament, a week away). It reads "May 8 - 10, 2026" everywhere the date shows. Search and the From / To filter find the gallery on any one of its days, combine the day filter with a text, place or tag search, and a month search ("August 2026") finds an event that runs into that month. Photos are still filtered by the day they were taken.
- **Needs attention:** nothing to do. Updating adds two columns to the database; a copy of the database is saved first.
- **Fixed:** an error in the browser console after closing the photo viewer and moving the mouse.

## 1.3.0

- **Sections: one gallery, several events.** For a gallery that holds, say, the four games of a tournament, keep each game in its own folder inside the gallery folder. In the gallery settings, **Sections** has **Find subfolders**; give each section a heading (for example "Game 1 vs Eagles"), put its YouTube link under it, and order them with the arrows. Visitors see each folder under its own heading with its video above its photos, a jump bar at the top, and a **Download** button for just that section (a ZIP of that game). Photos that sit outside any subfolder show last under "Other photos". Remove every section to go back to one grid. A gallery with no sections looks and works exactly as before.
- **Needs attention:** nothing to do. Updating adds one column to the database; a copy of the database is saved first, as for every schema change.
- **Fixed:** the word "null" no longer shows up in the admin pages (under the photographer's comments and under Database backups), and the file pickers on Settings and Statistics stay hidden under the content security policy.
- **Statistics:** Top viewed photos and Top downloads leave out photos that were removed, so they no longer take a spot with a broken thumbnail.

## 1.2.0

- **Template changes:** the example address in the `CAPTION_URL` description is generic now. Nothing to do.
- **Move your settings to another server.** Settings -> Site and appearance has **Export settings** (a file with the site name, theme, watermark, email and sign-in settings, no passwords) and **Load settings from a file**. Saved passwords on the new server are kept, and the sign-in test and "hide the password form" must be done again there.
- **No email until it is set up.** Nothing is queued or sent, and visitors are not offered the subscribe form, until alerts are on and both a mail server and a From address are filled in. The Overview shows "Incomplete" when alerts are on but not finished.
- **Email settings start empty.** The mail server and From address no longer default to the author's own; the test button says plainly when either is missing. Saved settings are unchanged.
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
