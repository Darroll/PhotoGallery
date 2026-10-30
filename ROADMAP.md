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

## Build order

Ordered so each step uses what the one before it built, and the quick wins come first.

1. **Link categories** (section 4). Small, self-contained, visible at once.
2. **Like counts** (section 1), keyed by IP or signed-in user (decided).
3. **Comments, moderation and log** (section 1). Needs Keycloak sign-in working, so the guided SSO step and test button come with it.
4. **Inbox** (section 3). Gathers comments, guest uploads and caption suggestions, so it comes after comments.
5. **Share button and QR** (section 3), then **invites** (section 5). Invites need working email, so the **email setup helper** (section 2) comes first.
6. **Setup wizard, simple mode, plain Unraid template** (sections 2 and 3). Last, because the wizard should walk through everything above and simple mode needs the final list of settings.
7. **Housekeeping** (section 6) runs alongside, not after.

## 1. Likes and feedback

**Likes**

- [ ] **Like count on every photo.** The like is the existing heart (decided). Show how many people liked it, next to the heart, on the grid and in the viewer.
- [ ] **Who a like belongs to (decided, Q10).** An anonymous like is tied to the visitor's IP address: one like per IP per photo, so clearing cookies does not allow a second one (IPv6 addresses are grouped by their /64 network). A signed-in user has one like per photo whatever IP they use, so the same person on two networks still counts once, and two signed-in people on one IP count twice. If a visitor signs in after liking anonymously from the same browser, the anonymous like becomes their signed-in like and is not counted twice. Known trade-off: people sharing one IP (a household) share a single anonymous like, and signing in avoids that. The IP is stored with the like and never displayed, and is covered by the privacy tools in section 6.
- [ ] **Most-liked view.** A "Most liked" sort and a top-liked strip on each gallery.
- [ ] **Likes only, never negatives.** No thumbs down, no dislike counts. A visitor can quietly remove their own like.
- [ ] **My likes page, with download.** A visitor sees their own liked photos in one place and downloads them (all, or ticked ones) as a ZIP, in the size and format they choose, with the RAW files if the gallery allows it. Gallery download rules still apply: license acceptance, watermark, share-link limits. Anonymous likes follow the visitor's IP (see above); signed-in likes follow the person across devices. (decided)
- [ ] **Per-gallery switches** for likes, like counts and comments.
- [ ] Liking must still work in protected and specific-people galleries, and the counts there are visible only to people who can open the gallery.

**Comments**

- [ ] **Comments on photos.** Shown immediately, no approval step (decided). Only people with a **verified email address** can comment. Verification is Keycloak's: sign-in goes through single sign-on and the provider's `email_verified` claim is respected (decided).
- [ ] **Show the display name, never the email address**, on comments. The email stays in the log. *proposed*
- [ ] **Flat comments only**, no threads and no reactions other than the like. Keeps it limited and positive. *proposed*
- [ ] **Edit or delete your own comment** for 15 minutes after posting (decided). The earlier text is kept in the log.
- [ ] **Admin can reply in the comments**, flat and marked as the photographer (decided).
- [ ] **Comment moderation.** Admin can review, hide and delete any comment; turn comments off per photo and per gallery; and block a person from commenting. Rate-limited and spam-guarded.
- [ ] **Comment log (who, what, when, where).** Every comment, edit, hide, delete and block is recorded: verified email and display name, the text (and previous text on edit), time, gallery and photo, and IP address and browser (the IP is stored, never displayed). The log is saved with the comment (decided). A deleted comment disappears from the page but its record stays in the admin log until its gallery is deleted. Viewable and searchable by the admin.
- [ ] **Email alert for new comments**, as a daily digest by default so it never floods the inbox (reuses the existing alerts).
- [ ] **Gallery message.** A single feedback / thank-you note to the photographer for the whole gallery.

## 2. Setup anyone can do

- [ ] **First-run setup wizard.** Choose the admin password, pick the photo folder from a list, name the site, create the first gallery. No config files. Optional later steps: email, sign-in (SSO), backups.
- [ ] **Sign-in made simple.** Single sign-on through Keycloak is the only way to comment (decided, Q11), and a non-technical person will not know how to set it up. So the wizard gets a guided SSO step in plain words: what to create in Keycloak, what to copy where, and a "test sign-in" button. The README gains a step-by-step Keycloak page with screenshots.
- [ ] **Email setup helper.** Invites, comment digests and subscriptions all need a mail relay, which is the hardest step for a non-technical person. Provide presets (for example Gmail or Outlook with an app password), a send-a-test button, and a plain explanation of any error. *proposed*
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
- [ ] **Off-server backup copy**, and **restore from a snapshot in the admin page** (with a safety copy made first).

## 4. Links and videos

- [ ] **Link categories.** Every link on a gallery has a category: **Video** (shown as the Videos grid above the photos, as today) or **External** (shown below the gallery). The admin picks the category when adding a link and can change it later. YouTube links default to Video. Existing links keep working: YouTube links become Video, other links become External. External links show as a preview card (picture and title) when one is available, otherwise as a plain row (decided). The buttons under the gallery title stay, for the links the admin chooses to show there (decided).
- [ ] **Previews for any link** (title, picture): Vimeo, Google Photos albums, news articles. The server fetches the preview once and caches it, and the picture is served from the gallery's own address, so visitors never contact the other site until they click.
- [ ] **Safe preview fetching.** The server refuses addresses on your own network and limits size and time, so a link cannot be used to probe the home network. The admin can switch preview fetching off, in keeping with "nothing leaves the server unless turned on". *proposed*
- [ ] **Links list on the gallery** with order, notes and "open in new tab".
- [ ] **Link health check.** Flag dead links for the admin and in the Inbox.
- Links only, no video uploads (decided).

## 5. Sharing with family

- [ ] **Invite by email** with a friendly message. (?) Q6. Note: if the invite link is personal and sent to one address, opening it can also count as verifying that address for comments.
- [ ] **"New photos" email** per gallery to everyone invited.
- [ ] **Family groups** shown by name in the invite flow (groups already exist).

## 6. Reliability, privacy and housekeeping

- [ ] Backup restore test; scheduled thumbnail cache cleanup.
- [ ] Handle renamed and moved files without losing likes, tags, captions and comments.
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
