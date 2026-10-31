# PhotoGallery roadmap

## Goal

A free, self-hosted, easy way to share photos with friends and family, and to share links about those galleries (YouTube videos and similar).

Principles that every feature is checked against:

1. **Easy to set up.** Someone who is not technical should get from install to a first shared gallery without editing files or environment variables.
2. **Super easy to administer.** The common jobs (new gallery, share it, approve something) take a few clicks and plain words. Advanced options stay folded away.
3. **Positive only.** Visitors can like photos and leave feedback. There is no dislike, downvote or rating below "like", anywhere.
4. **Limited social.** Likes and comments, nothing else: no followers, feeds, public profiles or ads.
5. **Private by default.** Nothing leaves the server unless the admin turned it on.

Legend: [x] done, [ ] to do, (?) waiting for a decision (see "Open questions"), *proposed* = suggested by Claude, not yet agreed.

### Definition of done (every item)

An item is finished only when it has: tests, a database migration if the model changed, a README update, a layout that works on a phone and in every theme, keyboard and screen-reader support, a plain-language label (no jargon), and, if it can fail, a line on the Overview health page. Then `./deploy.sh` and a push to GitHub.

## Already built

- [x] Galleries from library folders, public / unlisted / protected / specific people
- [x] Slideshow, sized lossless downloads, RAW pairing, ZIP, watermarks
- [x] Share links with expiry and download limits, guest uploads (moderated)
- [x] YouTube videos per gallery, other links as buttons
- [x] Favorites (heart) per visitor, reviewed by the photographer
- [x] Tags, search, themes, email alerts, subscriptions, captions and alt text
- [x] Single sign-on, groups, license agreement, visitor statistics
- [x] Health overview, automatic database backups, EF Core migrations
- [x] Community Apps packaging: public template, icon, maintainer profile, public GHCR image built by GitHub Actions, MIT license

## Unraid Community Apps listing (your steps)

Done in the repo and on GitHub: template `unraid/photogallery.xml`, icon, `ca_profile.xml`, **Publish image** workflow, public package `ghcr.io/darroll/photogallery`, MIT `LICENSE`. What is left needs your Unraid forum account:

- [x] **Open a support thread** on the Unraid forum (Docker Containers section) for PhotoGallery: what it is, screenshots, how to install, where to report bugs.
- [x] **Put the thread URL in the template.** Set `<Support>` in `unraid/photogallery.xml` (it points at GitHub issues now), commit and push.
- [ ] **Test the template on a clean setup.** Copy `unraid/photogallery.xml` to `/boot/config/plugins/dockerMan/templates-user/` on the server, Add Container -> photogallery, leave `ADMIN_PASSWORD` empty, confirm it pulls from ghcr.io, starts, and the password appears in the log.
- [ ] **Submit to Community Applications.** Post the repository (`https://github.com/Darroll/PhotoGallery`) in the Community Applications thread / submission form and answer the moderators' review comments.
- [ ] **Keep the image fresh.** Each push to `main` rebuilds `:latest`; check the Actions tab after a release (a failed build leaves users on the old image). Dependabot already covers NuGet and Docker.
- [ ] (optional) Replace the placeholder `unraid/icon.png` with your own artwork; add screenshots to the README for the listing.

## Order of work

Work is **grouped by feature area, and a group is finished before the next one starts**: all link work, then all favorite (like) work, then all comment work, and so on. Inside a group the steps run in dependency order, quick and low-risk steps first. Between groups, two rules decide the order: quick wins come first, and the data is protected before it becomes irreplaceable (backups and file-rename safety come before comments exist).

- **Effort** (rough size of one build, test and release cycle): **S** under half a day, **M** about a day, **L** two to three days, **XL** about a week.
- **Complexity** (risk of getting it wrong): **Low** routine; **Med** several parts or a database change; **High** security, privacy, data loss or tricky edge cases, so it gets extra tests and a careful review.
- Estimates are for planning only and will be corrected as work proves them wrong.

### Group A: Links and videos (about 2 to 3 days)

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 1 | Link categories: Video above, External below the gallery; migrate existing links | S | Low | none |
| 2 | Link previews with safe fetching and cached pictures (External cards) | M | High | 1 |
| 3 | Links list: order, notes, open in new tab; link health check | S | Low | 1, 2 |

### Group B: Favorites and likes (about 3 to 4 days)

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 4 | Like counts on the heart: one per IP or signed-in user, per-gallery switches, move existing favorites across | M | Med | none |
| 5 | Most-liked sort and top-liked strip | S | Low | 4 |
| 6 | My likes page with ZIP download of the visitor's own liked photos | M | Med | 4 |

### Group C: Protect the data (about 4 to 5 days)

Comments and likes will be content nobody can recreate, so this comes before them.

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 7 | Off-server backup copy, and restore from the admin page (safety copy first) | M | High | none |
| 8 | Keep likes, tags and captions when files are renamed or moved | L | High | none |

### Group D: Email (about 1 day)

A short group of its own because comment digests, invites and subscriptions all depend on it.

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 9 | Email setup helper: presets, send-a-test button, plain-language errors | M | Med | none |

### Group E: Comments (about 8 to 10 days)

Everything to do with comments, finished together. Steps 11 and 12 must never ship apart.

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 10 | Guided single sign-on step, "test sign-in" button and Keycloak how-to | M | Med | none |
| 11 | Comments core: post, display name, flat replies, admin reply, edit or delete own for 15 minutes, switches per photo and gallery, rate limit | L | High | 4, 10 |
| 12 | Moderation, comment log (who, what, when, where; IP stored not shown), block a person | L | High | 11 |
| 13 | Gallery message (thank-you note to the photographer) | S | Low | 11 |
| 14 | New-comment email, as a daily digest | S | Low | 9, 11 |
| 15 | Privacy tools: export or delete one person's data (likes and comments), privacy notice, delete-gallery cleanup | M | Med | 4, 11 |

### Group F: Sharing with family (about 4 to 5 days)

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 16 | Share button: link, ready-to-send message, QR code, printable QR card | M | Low | none |
| 17 | Invite by email, family groups in the invite flow, "new photos" email | L | High | 9, 16, Q6 |

### Group G: Easy setup and administration (about 8 to 9 days)

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 18 | Inbox: uploads, caption suggestions, comments, failed mail, dead links in one place | M | Med | 3, 11 |
| 19 | Settings in the app instead of container variables (container value wins, shown locked) | M | Med | none |
| 20 | First-run setup wizard (password, folder, site name, first gallery; optional email, sign-in, backups) | L | Med | 9, 10, 19 |
| 21 | Simple and advanced mode; create a gallery from a folder in one step | M | Med | 19 |
| 22 | Plain-language Unraid template (public Community Apps template exists; wording pass still open), install guide with screenshots, getting-started checklist, "is my site reachable?" check | M | Low | 20 |
| 23 | Sample gallery, built-in help on each admin page, update notice | M | Low | 21 |
| 24 | Hardening checks and scheduled cache cleanup on the Overview page | S | Low | none |

### Group H: Housekeeping, then ideas (about 4 to 5 days, plus ideas)

| # | Work | Effort | Complexity | Depends on |
|---|---|---|---|---|
| 25 | Admin audit log (who changed what) | M | Med | 12 |
| 26 | Accessibility pass on every page and theme | L | Med | 21 |
| 27 | Other install routes (Q4) | M | Med | 22 |
| 28 | "Later / ideas" (map, memories, photo of the week, keepsake page, phone-app install, translations), chosen one at a time | M to L each | varies | |

Highest risk, so most careful: #2 (fetching other sites), #7 (restore), #8 (file identity), #11 and #12 (comments and the log), #17 (invites and access).

If time is short, the smallest set that delivers the vision is **#1, #4, #6, #7, #10 to #12 and #16**: link categories, likes with counts, downloadable liked photos, safe backups, comments with moderation and sign-in, and a one-click share.

## 1. Likes and feedback

**Likes**

- [x] **Like count on every photo.** The like is the existing heart (decided). Show how many people liked it, next to the heart, on the grid and in the viewer.
- [x] **Who a like belongs to (decided, Q10).** An anonymous like is tied to the visitor's IP address: one like per IP per photo, so clearing cookies does not allow a second one (IPv6 addresses are grouped by their /64 network). A signed-in user has one like per photo whatever IP they use, so the same person on two networks still counts once, and two signed-in people on one IP count twice. If a visitor signs in after liking anonymously from the same browser, the anonymous like becomes their signed-in like and is not counted twice. Known trade-off: people sharing one IP (a household) share a single anonymous like, and signing in avoids that. The IP is stored with the like and never displayed, and is covered by the privacy tools in section 6.
- [x] **Most-liked view.** A "Most liked" sort and a top-liked strip on each gallery.
- [ ] **Likes only, never negatives.** No thumbs down, no dislike counts. A visitor can quietly remove their own like.
- [x] **My likes page, with download.** A visitor sees their own liked photos in one place and downloads them (all, or ticked ones) as a ZIP, in the size and format they choose, with the RAW files if the gallery allows it. Gallery download rules still apply: license acceptance, watermark, share-link limits. Anonymous likes follow the visitor's IP (see above); signed-in likes follow the person across devices. (decided)
- [x] **Per-gallery switches** for likes and like counts. (The comments switch comes with comments.)
- [ ] Liking must still work in protected and specific-people galleries, and the counts there are visible only to people who can open the gallery.

**Comments**

- [x] **Comments on photos.** Shown immediately, no approval step (decided). Only people with a **verified email address** can comment. Verification is Keycloak's: sign-in goes through single sign-on and the provider's `email_verified` claim is respected (decided).
- [x] **Show the display name, never the email address**, on comments. The email stays in the log. *proposed*
- [x] **Flat comments only**, no threads and no reactions other than the like. Keeps it limited and positive. *proposed*
- [x] **Edit or delete your own comment** for 15 minutes after posting (decided). The earlier text is kept in the log.
- [x] **Admin can reply in the comments**, flat and marked as the photographer (decided).
- [x] **Comment moderation.** Admin can review, hide and delete any comment; turn comments off per photo and per gallery; and block a person from commenting. Rate-limited and spam-guarded.
- [x] **Comment log (who, what, when, where).** Every comment, edit, hide, delete and block is recorded: verified email and display name, the text (and previous text on edit), time, gallery and photo, and IP address and browser (the IP is stored, never displayed). The log is saved with the comment (decided). A deleted comment disappears from the page but its record stays in the admin log until its gallery is deleted. Viewable and searchable by the admin.
- [ ] **Email alert for new comments**, as a daily digest by default so it never floods the inbox (reuses the existing alerts).
- [ ] **Gallery message.** A single feedback / thank-you note to the photographer for the whole gallery.

## 2. Setup anyone can do

- [ ] **First-run setup wizard.** Choose the admin password, pick the photo folder from a list, name the site, create the first gallery. No config files. Optional later steps: email, sign-in (SSO), backups.
- [x] **Sign-in made simple.** Single sign-on through Keycloak is the only way to comment (decided, Q11), and a non-technical person will not know how to set it up. Built as Admin -> Settings -> Sign-in (SSO): what to create in Keycloak with this site's addresses to copy, a connection check, a "test sign-in" button, no restart needed, and the password form can only be hidden after a test as an admin worked. Still to do: the same steps inside the first-run wizard (#20), and Keycloak screenshots in the README.
- [x] **Email setup helper.** Invites, comment digests and subscriptions all need a mail relay, which is the hardest step for a non-technical person. Provide presets (for example Gmail or Outlook with an app password), a send-a-test button, and a plain explanation of any error. *proposed*
- [ ] **Settings in the app instead of container variables** where possible (sizes, rescan interval, backup retention). Rule: a value set in the container always wins and is shown as locked in the app.
- [ ] **Plain-language Unraid template.** Short field descriptions, safe defaults, only a handful of fields shown.
- [ ] **"Is my site reachable?" check** on the Overview page, with a plain explanation when it is not.
- [ ] **Getting-started checklist** on the Overview page for a new install (create a gallery, share it, set up email, back up) that disappears when done. *proposed*
- [ ] **Sample gallery** created on request, so a new admin can try every feature before using real photos. *proposed*
- [ ] **Simple install guide** with screenshots, in the README and in the app's Help page.
- [ ] **Update notice** on the Overview page when a newer version is available. *proposed*
- [ ] (?) Q4: other install routes (plain `docker compose`, Synology, a Windows/Mac one-click app).

## 3. Easy administration

- [ ] **Share button.** One click gives the link, a copy-ready message, a QR code and a printable QR card; choose "anyone with the link" or "needs a password" in plain words. For *Specific people* galleries it leads into the invite flow (section 5).
- [ ] **Simple mode / advanced mode.** Simple shows the five things most people need; advanced shows today's full settings.
- [ ] **Create a gallery from a folder in one step**, with sensible defaults, then share.
- [ ] **Inbox.** One place for everything waiting on you: guest uploads, caption suggestions, new comments, failed emails, dead links.
- [ ] **Built-in help** on each admin page ("What does this do?").
- [x] **Off-server backup copy** (download, or `BACKUP_COPY_DIR`), and **restore from a snapshot in the admin page** (with a safety copy made first).

## 4. Links and videos

- [x] **Link categories.** Every link on a gallery has a category: **Video** (shown as the Videos grid above the photos, as today) or **External** (shown below the gallery). The admin picks the category when adding a link and can change it later. YouTube links default to Video. Existing links keep working: YouTube links become Video, other links become External. External links show as a preview card (picture and title) when one is available, otherwise as a plain row (decided). The buttons under the gallery title stay, for the links the admin chooses to show there (decided).
- [x] **Previews for any link** (title, picture): Vimeo, Google Photos albums, news articles. The server fetches the preview once and caches it, and the picture is served from the gallery's own address, so visitors never contact the other site until they click.
- [x] **Safe preview fetching.** The server refuses addresses on your own network and limits size and time, so a link cannot be used to probe the home network. The admin can switch preview fetching off, in keeping with "nothing leaves the server unless turned on". *proposed*
- [x] **Links list on the gallery** with order, notes and "open in new tab".
- [x] **Link health check.** Flag dead links for the admin and in the Inbox.
- Links only, no video uploads (decided).

## 5. Sharing with family

- [ ] **Invite by email** with a friendly message. (?) Q6. Note: if the invite link is personal and sent to one address, opening it can also count as verifying that address for comments.
- [ ] **"New photos" email** per gallery to everyone invited.
- [ ] **Family groups** shown by name in the invite flow (groups already exist).

## 6. Reliability, privacy and housekeeping

- [ ] Backup restore test; scheduled thumbnail cache cleanup.
- [x] **Renamed and moved files keep their likes, tags and captions** (file fingerprint; photos follow the file within and across galleries; details of removed photos held 90 days). Comments will attach to the photo the same way once they exist.
- [ ] Admin audit log (who changed what), alongside the comment log.
- [ ] **Privacy tools.** With comments the app now holds email addresses and names. Add: export everything held about one person, delete a person (their likes and comments), and a short editable privacy notice linked from the footer next to the license. *proposed*
- [ ] **Delete gallery** also removes its comments and likes, and says so in the confirmation.
- [ ] Accessibility pass (keyboard, screen reader, contrast) on all themes.
- [ ] Hardening checks on the Overview page (published port, default password).

## Later / ideas

- [ ] Map of where a gallery's photos were taken (admin only or opt-in)
- [ ] "Memories" or "on this day" page
- [ ] Photo-of-the-week on the home page
- [ ] Download a gallery's likes and comments as a small keepsake page
- [ ] Install on a phone like an app (home-screen icon, works offline for already-viewed photos) *proposed*
- [ ] Translations, for relatives who read another language *proposed*

## Open questions

Answers go here as they are decided.

- **Q1. Decided:** a like is the heart; counts are shown.
- **Q2/Q3. Decided:** comments appear at once, from verified email addresses only; admin can monitor, remove, switch off per photo or gallery, and block people; every comment is logged with who, what, when, where.
- **Q4.** Which install routes matter to you beyond Unraid?
- **Q5. Decided:** links only.
- **Q6.** Invites: personal links that need no account, or sign-in?
- **Q7. Decided:** verification follows Keycloak (`email_verified`); commenting needs single sign-on.
- **Q8. Decided:** the log is saved with the comment; the IP address is stored but never rendered.
- **Q9. Decided:** External links use preview cards when available, else a plain row; the buttons under the title stay.
- **Q10. Decided:** anonymous likes count once per IP; signed-in users count once per account, whatever their IP.
- **Q11. Decided:** Keycloak only; the setup wizard gets a guided SSO step.
- **Q12. Decided:** the admin may reply, flat, marked as the photographer.
- **Q13. Decided:** commenters may edit or delete their own comment for 15 minutes; the log keeps the earlier text.
