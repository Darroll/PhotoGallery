# Changelog

Newest first. "Needs attention" marks anything an existing user must do when updating.

## 2.6.0 (not released yet)

- **Teams have seasons.** Each season of a team (Fall 2026, Spring 2027) has its own roster and its own jerseys, for a year with special pink jerseys. Settings, **Teams** shows one card per team with its seasons inside, newest first. **Start season** on a team's card begins the next season with the last season's roster and jerseys, so only the players who left or joined change. A gallery picks the team season in its Players section: the AI looks for that season's numbers in that season's jerseys, and the home page lists the gallery under that season (the gallery's own Season box still wins, and galleries without a team still go by the event date). The home page's team chips, the search team filter and the admin list's team filter cover every season of a team.
- **A girl is one player across teams and seasons.** Type her name the same way on every roster she is on (Reagan as #42 on OCSS and #11 on East Ridge, or a new season), and she is one player: a parent who follows her on any team gets her new photos from all of them in one email, and stopping on any team stops all of them. In each gallery her chip shows that roster's number. Names still stay off the site unless the team puts names in tags; two different girls with the same name need names that differ ("Emma S." and "Emma K."). Players without a name stay separate.
- Updating adds a season to each team (empty for the teams you have now; type one in to start using seasons) and links players with the same name on different rosters. A copy of the database is saved first. Photo tags do not change.

## 2.5.0 (not released yet)

- **Galleries are grouped by season on the home page.** This season's galleries come first and open; earlier seasons are folded under their heading ("Spring 2026, 4 galleries") and open with a tap, so parents find this season's games straight away. A gallery's season comes from its event date (or the day it was made): Fall is August to December and Spring January to July. Settings, **Site and appearance** can switch to August-to-July seasons ("2026-27 season"), calendar years, or off (one list as before). To name a season yourself, such as "Indoor 2027", type it in the gallery's new **Season** box; galleries with the same season are listed together. A preset carries the Season box too. Inside each season the galleries keep the home page order.
- **Team filter.** When the home page has team galleries (the team set in a gallery's Players section) next to galleries of another team or of none, a row of team chips shows only that team's galleries. The choice is kept in the address (`/?team=3`, so it can be shared) and remembered on that device. Search has a team filter under Filters, and the admin Galleries list filters by team and season and shows both on each row.
- Updating adds one column to the database; a copy of the database is saved first.

## 2.4.0 (not released yet)

- **Gallery presets.** Set up one gallery the way you like, then press **Save as preset** at the bottom of its settings. New galleries can start with those settings: who can open it and its password, invited groups, tags, location, downloads, RAW and curated only, likes and comments, watermark, the team and its jersey, automatic player checks, parent suggestions and follow a player. The new gallery box on the Galleries page has a preset picker and says what the preset sets, so a weekly game gallery needs only a folder (the title starts as the folder name), and its new photos are checked for players straight away when the preset says so. One preset can be the one new galleries start with; manage them in Settings, **Gallery presets**. Title, dates, cover, links and sections are never part of a preset, and changing a preset does not change galleries already made with it.
- Presets are kept with the other site settings (and in a settings export). The database does not change.

## 2.3.2 (not released yet)

- **The right-click fix menu in a gallery's Players section stays open.** It closed at the slightest scroll, including the page settling as photos load, so it could vanish as soon as it opened. It now closes only when you really scroll away (or click elsewhere, or press Escape). The database does not change.

## 2.3.1 (not released yet)

- **Follow a player is one site-wide switch.** Turn it on once at the top of Settings, **Teams**, and every team gallery offers it. A gallery's Players section can still turn it off (or on) for that gallery alone. A follow is for the player (team and number), so one follow covers every gallery of the team.
- **Follow from more places.** In the photo viewer, the players under a photo have a ☆ to follow them. Signed-in visitors get a **★ My players** page (in the header) listing the teams they can follow, with a tap to follow or stop. Every email links to it.

## 2.3.0 (not released yet)

- **Parents can follow a player.** A new switch in each gallery's Players section, off by default, lets signed-in visitors pick a player's chip and tap **Follow #10**. They then get an email, once a day at most, listing the new photos of each player they follow, with a link that opens the gallery on that player. Only approved tags count, after they have stood for 30 minutes, and only in the team's galleries that allow following and that the visitor can open. Players are named by number unless the team uses names in tags. Every email has a stop link, and the privacy tools export and delete follows. Needs sign-in (SSO) and email alerts with the site address.
- Updating adds a table and two columns to the database; a copy of the database is saved first. Tags already on photos count as old, so nobody is emailed about them.

## 2.2.4 (not released yet)

- **Tag suggestions stay with the gallery's team.** The + tag box on a gallery or a photo now suggests ordinary tags plus the player tags of that gallery's team only. A gallery without a team suggests no player tags. Tags already on a photo stay as they are, and the Tags settings page still lists every tag. The database does not change.

## 2.2.3 (not released yet)

- **Tag players while looking at a photo.** The photo viewer shows a **Players** row under each photo with everyone tagged in it, apart from the other tags. The admin sees suggestions still waiting (dashed, with ✓ and ×) and **+ Tag players**, which opens the roster: tap every player in the photo, tap a lit one to untag it, and **Save**. Numbers not on the roster can be typed there too and wait until the player is added.
- **Review grid.** The suggestions waiting in a gallery's Players section are now large photo cards that show the tags each photo already has. Click photos to select them (Shift-click for a run, or **Select all**), then **Approve selected**, **Reject selected**, **Add to selected** (tag one more player on all of them) or **Change to** (the same misread number on several photos). The right-click fix menu still works on each card.
- **Parents can suggest player tags.** A new switch in each gallery's Players section, off by default, lets signed-in visitors tap **Tag a player** under a photo and pick the players they see. Their suggestions wait in the review grid marked *from* their name, and nothing is tagged until you approve it. They see jersey numbers only, unless the team uses names in tags. Needs sign-in (SSO) set up.
- Updating adds one column to the database; a copy of the database is saved first.

## 2.2.2 (not released yet)

- **Fixing misread numbers is quicker.** In a gallery's Players section, right-click a photo (in the review list or under *Numbers not on the roster*) for a menu with your roster numbers: click the right one, or type another and press Enter, or choose **Not a player**; suggestions also have **Approve** and **Reject** there. The photo is marked as fixed in place, so the list stays put while you fix a handful. A number read wrongly on several photos (2 for 12) can be moved in one go: tick those photos and use **Change ticked photos to** with the right number. A photo can also be moved to a number that is not on the roster yet (for example when you do not know the player's name): it waits with that number's other photos under *Numbers not on the roster*, and is tagged as soon as you add the player. Up to 60 photos are shown for each number not on the roster. Shift and right-click still opens the browser's own menu. The database does not change.

## 2.2.1 (not released yet)

- **Save to phone works on Android.** Android's Chrome shares at most 10 files and 50 MB at a time, so it refused larger batches and big edited photos ("Your phone would not open the share sheet"). Photos now go 10 at a time and under 50 MB per batch. Edited photos saved as PNG, and any original over 20 MB, are sent to the phone as a high-quality JPEG (up to 4096 px) instead of the full file. If the phone still refuses, the message now says why.

## 2.2.0

- **Saving to a phone takes fewer taps.** On a phone that can save to its photo app (iPhone and Android browsers), the photo viewer's **Download** button is now **Save**: one tap puts that photo in the photo app, with **Sizes and formats…** there for the full choices. When choosing photos, a **long press** on a photo starts choosing (no need to find **Select** first), and the bar at the bottom has **Save to phone** straight away. A player's button reads **Save all N photos of #10** and goes straight to saving. When the photos are ready quickly, the phone's share sheet opens without the extra "Save the photo" tap. After the chosen photos are saved, the choice is cleared. Where the license must be agreed first, the download sheet opens as before. The database does not change.

## 2.1.2 (not released yet)

- **RAW files come along by default.** In a gallery that allows RAW downloads, *Include the RAW files* in the download sheet is now ticked by default, so a ZIP of a player, a section, picked photos or the whole gallery holds each edited photo plus its RAW file (in a RAW folder) unless the visitor unticks it. The admin sees the option in every gallery. The database does not change.

## 2.1.1

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
