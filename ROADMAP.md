# PhotoGallery roadmap

## Goal

A free, self-hosted, easy way to share photos with friends and family, and to share links about those galleries (YouTube videos and similar).

Principles that every feature is checked against:

1. **Easy to set up.** Someone who is not technical should get from install to a first shared gallery without editing files or environment variables.
2. **Super easy to administer.** The common jobs (new gallery, share it, approve something) take a few clicks and plain words. Advanced options stay folded away.
3. **Positive only.** Visitors can like photos and leave feedback. There is no dislike, downvote or rating below "like", anywhere.
4. **Limited social.** Likes and comments, nothing else: no followers, feeds, public profiles or ads.
5. **Private by default.** Nothing leaves the server unless the admin turned it on.

Legend: [x] done, [ ] to do, (?) waiting for a decision (see "Open questions").

## Already built

- [x] Galleries from library folders, public / unlisted / protected / specific people
- [x] Slideshow, sized lossless downloads, RAW pairing, ZIP, watermarks
- [x] Share links with expiry and download limits, guest uploads (moderated)
- [x] YouTube videos per gallery, other links as buttons
- [x] Favorites (heart) per visitor, reviewed by the photographer
- [x] Tags, search, themes, email alerts, subscriptions, captions and alt text
- [x] Single sign-on, groups, license agreement, visitor statistics
- [x] Health overview, automatic database backups, EF Core migrations

## 1. Likes and feedback (next)

- [ ] **Like count on every photo.** Show how many people liked it, next to the heart, on the grid and in the viewer. (?) Q1
- [ ] **Most-liked view.** A "Most liked" sort and a top-liked strip on each gallery.
- [ ] **Likes only, never negatives.** No thumbs down, no dislike counts, no "unlike" shaming: a visitor can remove their own like quietly.
- [ ] **Comments on photos.** A visitor adds a name and a short comment. (?) Q2, Q3
- [ ] **Gallery message.** A single feedback / thank-you note to the photographer for the whole gallery.
- [ ] **Comment moderation.** Admin can hide or delete any comment, block a name, and turn comments off per gallery. Rate-limited and spam-guarded.
- [ ] **Email alert for new comments** (reuses the existing alerts).
- [ ] **Per-gallery switches** for likes, like counts and comments.

## 2. Setup anyone can do

- [ ] **First-run setup wizard.** Choose the admin password, pick the photo folder from a list, name the site, create the first gallery. No config files.
- [ ] **Settings in the app instead of container variables** where possible (sizes, rescan interval, backup retention).
- [ ] **Plain-language Unraid template.** Short field descriptions, safe defaults, only a handful of fields shown.
- [ ] **"Is my site reachable?" check** on the Overview page, with a plain explanation when it is not.
- [ ] **Simple install guide** with screenshots, in the README and in the app's Help page.
- [ ] (?) Q4: other install routes (plain `docker compose`, Synology, a Windows/Mac one-click app).

## 3. Easy administration

- [ ] **Share button.** One click gives the link, a copy-ready message, and a QR code; choose "anyone with the link" or "needs a password" in plain words.
- [ ] **Simple mode / advanced mode.** Simple shows the five things most people need; advanced shows today's full settings.
- [ ] **Create a gallery from a folder in one step**, with sensible defaults, then share.
- [ ] **Inbox.** One place for everything waiting on you: guest uploads, comments, caption suggestions.
- [ ] **Built-in help** on each admin page ("What does this do?").
- [ ] **Off-server backup copy**, and **restore from a snapshot in the admin page**.

## 4. Links and videos

- [ ] **Any link with a preview** (title, picture): Vimeo, Google Photos albums, news articles, not only YouTube.
- [ ] **Links list on the gallery** with order, notes and "open in new tab".
- [ ] **Link health check.** Flag dead links for the admin.
- [ ] (?) Q5: upload short video clips directly, or keep to links only.

## 5. Sharing with family

- [ ] **Invite by email** with a friendly message and a sign-in-free personal link. (?) Q6
- [ ] **"New photos" email** per gallery to everyone invited.
- [ ] **Family groups** shown by name in the invite flow (groups already exist).
- [ ] **Printable QR card** for a gallery (for events and parties).

## 6. Reliability and housekeeping

- [ ] Backup restore test; scheduled thumbnail cache cleanup.
- [ ] Handle renamed and moved files without losing likes, tags and captions.
- [ ] Admin audit log (who changed what).
- [ ] Accessibility pass (keyboard, screen reader, contrast) on all themes.
- [ ] Hardening checks on the Overview page (published port, default password).

## Later / ideas

- [ ] Map of where a gallery's photos were taken (admin only or opt-in)
- [ ] "Memories" or "on this day" page
- [ ] Photo-of-the-week on the home page
- [ ] Download a gallery's likes and comments as a small keepsake page

## Open questions

Answers go here as they are decided.

- **Q1.** Is a "like" the same thing as today's heart / favorite, or a separate action?
- **Q2.** Who may comment: anyone with the link (name only), or only signed-in people?
- **Q3.** Should comments appear immediately or wait for approval?
- **Q4.** Which install routes matter to you beyond Unraid?
- **Q5.** Video clips: links only, or uploads too?
- **Q6.** Invites: personal links that need no account, or sign-in?
