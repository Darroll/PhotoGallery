'use strict';
// ---------- helpers ----------
const $ = (s) => document.querySelector(s);
function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'checked' || k === 'value' || k === 'disabled' || k === 'selected') el[k] = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) if (kid !== null && kid !== undefined && kid !== false) el.append(kid.nodeType ? kid : document.createTextNode(kid));
  return el;
}
async function api(path, opts = {}) {
  const init = { method: opts.method || 'GET', headers: { 'X-Requested-With': 'pg' }, credentials: 'same-origin' };
  if (opts.body !== undefined) { init.body = JSON.stringify(opts.body); init.headers['Content-Type'] = 'application/json'; }
  if (opts.form) init.body = opts.form;
  const r = await fetch(path, init);
  let data = null;
  const ct = r.headers.get('content-type') || '';
  if (ct.includes('json')) data = await r.json().catch(() => null);
  else if (r.status !== 204) data = await r.text().catch(() => null);
  return { ok: r.ok, status: r.status, data };
}
let toastTimer;
function toast(msg) {
  const t = $('#toast'); t.textContent = msg; t.classList.remove('hidden');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add('hidden'), 3500);
}
const fmtBytes = (n) => n <= 0 ? '0 KB' : n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB';
const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
const fmtLabels = { png: 'PNG (lossless)', webp: 'WebP (lossless)', tif: 'TIFF (lossless)' };
let site = { title: 'Photos', sizes: [], formats: [] };
let me = null, signinFailed = false;
const themeKey = 'pg-theme';
// The theme the visitor sees: their own pick (when the site allows it and it still exists), else the site default.
function currentTheme() {
  const t = site.theme || {}; let c = t.default || 'auto';
  if (t.allowChoice) { try { const s = localStorage.getItem(themeKey); if (s && (t.themes || []).some(x => x.id === s)) c = s; } catch { } }
  return c;
}
function applyTheme() {
  const c = currentTheme(), root = document.documentElement;
  if (c === 'auto') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', c);
}
// Signed-in people (those with an account id) also keep the pick on the server, so it follows them to every device.
const hasAccountPrefs = () => !!(me && me.preferences);
function saveTheme(id) {
  if (!hasAccountPrefs()) return;
  me.preferences.theme = id;
  api('/api/me/preferences', { method: 'PUT', body: { theme: id } }).then(r => { if (!r.ok) toast('Could not save your theme to your account'); });
}
// On load: the account's saved theme wins over this browser's; if the account has none yet, this browser's pick is saved to it.
function syncAccountTheme() {
  const t = site.theme || {};
  if (!hasAccountPrefs() || !t.allowChoice) return;
  const known = (id) => (t.themes || []).some(x => x.id === id);
  const saved = me.preferences.theme;
  if (saved && known(saved)) { try { localStorage.setItem(themeKey, saved); } catch { } return; }
  let local = null; try { local = localStorage.getItem(themeKey); } catch { }
  if (local && known(local)) saveTheme(local);
}
function themePicker() {
  const t = site.theme;
  if (!t || !t.allowChoice || !(t.themes || []).length) return null;
  const sel = h('select', { class: 'theme-pick', 'aria-label': 'Theme', title: 'Theme' }, t.themes.map(x => h('option', { value: x.id, text: x.label, selected: x.id === currentTheme() })));
  sel.addEventListener('change', () => {
    try { localStorage.setItem(themeKey, sel.value); } catch { }
    applyTheme();
    saveTheme(sel.value);
  });
  return sel;
}
let license = { enabled: false, require: false };
// Acceptance is recorded on the server (against the account, or the browser's visitor id); the server reports whether this visitor has accepted the current wording.
async function recordLicenseAcceptance() {
  const r = await api('/api/license/accept', { method: 'POST', body: { version: license.version } });
  if (r.ok) { license.accepted = true; return true; }
  toast(r.status === 409 ? 'The license has changed. Reload the page to read it.' : 'Could not record your agreement. Try again.');
  return false;
}
// Tag colours. A tag the admin has not coloured gets one picked from its name, so every tag has a colour and a tag keeps the same one everywhere.
const tagPalette = ['#e5484d', '#f76b15', '#e0a000', '#30a46c', '#12a594', '#0091ff', '#3e63dd', '#8e4ec6', '#d6409f', '#6e7b8b'];
let tagColorMap = {};   // lower-case tag name -> colour the admin chose
function learnTagColors(map) { if (map && typeof map === 'object') Object.assign(tagColorMap, map); }
function tagColor(name) {
  const key = String(name).toLowerCase();
  if (tagColorMap[key]) return tagColorMap[key];
  let n = 0; for (const c of key) n = (n * 31 + c.charCodeAt(0)) >>> 0;
  return tagPalette[n % tagPalette.length];
}
// A coloured #label. The colour tints the background and border; the text stays the theme's own colour so it is always readable.
function tagPill(name, extra) {
  const el = h('span', { class: 'tag-pill' + (extra ? ' ' + extra : ''), text: '#' + name });
  el.style.setProperty('--tag-c', tagColor(name));
  return el;
}
// A row of tag pills. With `edit` ({ title, url, onSaved }) the admin also gets a + that opens a small editor, so tags can be added
// right where they are shown: on a gallery card, a search result, the gallery page, the photo viewer. url is the PUT endpoint that replaces the tags.
function tagLine(names, edit) {
  const canEdit = !!(edit && ((me && me.admin) || (G && G.admin)));
  if (!(names && names.length) && !canEdit) return null;
  let current = [...(names || [])];
  const el = h('div', { class: 'tag-line' });
  // replaceChildren turns a null into the text "null", so the button is only passed when there is one
  const draw = () => el.replaceChildren(...current.map(n => tagPill(n)), ...(canEdit ? [h('button', {
    type: 'button', class: 'tag-add-btn', text: current.length ? '+' : '+ Tag', title: 'Add or change tags', 'aria-label': 'Add or change tags',
    // the + often sits inside a card that is a link, so it must not follow it
    onclick: (e) => {
      e.preventDefault(); e.stopPropagation();
      tagEditSheet(edit.title, current, async (list) => {
        const r = await api(edit.url, { method: 'PUT', body: { names: list } });
        if (!r.ok || !Array.isArray(r.data)) return false;
        current = r.data; draw(); if (edit.onSaved) edit.onSaved(r.data);
        return true;
      });
    } })] : []));
  draw();
  return el;
}
const galleryTagEdit = (g) => g && g.id != null ? { title: 'Tags for ' + g.title, url: `/api/admin/galleries/${g.id}/tags`, onSaved: (t) => { g.tags = t; } } : null;
const photoTagEdit = (p) => ({ title: 'Tags for ' + (p.name || 'this photo'), url: `/api/admin/photos/${p.id}/tags`, onSaved: (t) => { p.tags = t; } });

// Admin: a small editor for one gallery's or photo's tags. save(names) returns true when it worked.
function tagEditSheet(title, names, save) {
  closeSheet();
  const editor = tagEditor(names, null, { open: true });
  const msg = h('span', { class: 'muted' });
  const submit = async () => {
    if (!await save(editor.names())) { msg.textContent = 'Could not save. Try again.'; return; }
    closeSheet(); toast('Tags saved');
  };
  const sheet = h('div', { id: 'sheet', class: 'sheet-backdrop', onclick: (e) => { if (e.target.id === 'sheet') closeSheet(); } },
    h('div', { class: 'sheet', role: 'dialog', 'aria-label': 'Edit tags' },
      h('div', { class: 'sheet-grab' }), h('h3', { text: title }),
      h('div', { class: 'sheet-field' }, h('span', { text: 'Tags' }), editor),
      h('p', { class: 'muted', text: 'Type a tag and press Enter, or click a suggestion. Click x on a tag to remove it.' }), msg,
      h('div', { class: 'row sheet-actions' }, h('button', { class: 'primary', text: 'Save', onclick: submit }), h('button', { text: 'Close', onclick: closeSheet }))));
  sheet._key = (e) => { if (e.key === 'Escape') { e.stopImmediatePropagation(); e.preventDefault(); closeSheet(); } };
  document.addEventListener('keydown', sheet._key, true);
  document.body.append(sheet);
}
const fmtDate = (s) => s ? new Date(/Z$|[+-]\d\d:\d\d$/.test(s) ? s : s + 'Z').toLocaleString() : '';

// ---------- routing ----------
let dirtyGuard = null; // set by pages with unsaved changes: returns true when it is OK to leave
let galleryView = null; // slug of the gallery grid currently rendered, so photo URLs can open/close the viewer without re-rendering
async function route(force = false) {
  dirtyGuard = null;
  closeSheet();
  const parts = location.pathname.split('/').filter(Boolean);
  const app = $('#app');
  if (parts[0] === 'g' && parts[1]) {
    const slug = decodeURIComponent(parts[1]);
    const pid = parts[2] === 'p' && parts[3] ? Number(parts[3]) : null;
    if (!force && galleryView === slug && G && G.slug === slug) { syncViewer(pid); return; }
    closeLightbox(); galleryView = null; app.replaceChildren(); document.title = site.title;
    await viewGallery(app, slug);
    if (galleryView === slug && pid) syncViewer(pid, 'direct');
    return;
  }
  galleryView = null; closeLightbox(); app.replaceChildren();
  document.title = site.title;
  if (parts[0] === 'admin' && parts[1] === 'stats') return viewStats(app);
  if (parts[0] === 'admin' && parts[1] === 'settings') return viewSettings(app, parts[2]);
  if (parts[0] === 'admin' && parts[1] === 'galleries' && parts[2]) return viewGallerySettings(app, Number(parts[2]));
  if (parts[0] === 'admin' && parts[1] === 'galleries') return viewAdmin(app);
  if (parts[0] === 'admin') return viewOverview(app);
  if (parts[0] === 'license') return viewLicense(app);
  if (parts[0] === 'search') return viewSearch(app);
  if (parts[0] === 'expired') document.title = 'Link unavailable - ' + site.title;
  if (parts[0] === 'expired') return app.append(h('div', { class: 'center' }, h('h1', { text: 'Link unavailable' }), h('p', { class: 'sub', text: 'This share link has expired or was revoked.' })));
  return viewHome(app);
}
window.addEventListener('popstate', () => route());
window.addEventListener('beforeunload', (e) => { if (dirtyGuard && !dirtyGuard(true)) { e.preventDefault(); e.returnValue = ''; } });
// old bookmarks like /#/g/slug -> /g/slug
if (location.hash.startsWith('#/')) history.replaceState(null, '', location.hash.slice(1));
// in-app links navigate without a page load
document.addEventListener('click', (e) => {
  const a = e.target.closest && e.target.closest('a[href]');
  if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || a.target === '_blank' || a.hasAttribute('download')) return;
  const href = a.getAttribute('href');
  if (href === '/' || /^\/(g|admin|expired|license|search)(\/|$)/.test(href)) {
    e.preventDefault();
    if (dirtyGuard && !dirtyGuard()) return;
    go(href);
  }
});
function go(path) { if (path !== location.pathname) history.pushState(null, '', path); route(); window.scrollTo(0, 0); }

function renderFooter() {
  const f = $('#foot'); if (!f) return;
  const ver = h('span', { text: `v${site.version || '?'}${site.commit && site.commit !== 'dev' ? ' \u00B7 ' + site.commit : ''}`, title: site.built ? `Built ${site.built}` : 'Development build' });
  const right = h('div', { class: 'foot-info' }, ...[license.enabled ? h('a', { class: 'foot-link', href: '/license', text: license.title || 'License' }) : null, license.enabled ? ' \u00B7 ' : null, ver].filter(Boolean));
  f.replaceChildren(...[site.subscribe ? subscribeForm() : null, right].filter(Boolean));
}

async function viewLicense(app) {
  document.title = (license.title || 'License') + ' - ' + site.title;
  if (!license.enabled) return app.append(h('div', { class: 'center' }, h('h1', { text: 'License' }), h('p', { class: 'sub', text: 'There is no license agreement for this site.' })));
  app.append(h('div', { class: 'license-page' }, h('h1', { text: license.title }), h('div', { class: 'license-text', text: license.text }),
    h('p', {}, h('a', { class: 'btn', href: '/', text: 'Back to galleries' }))));
}

function renderHeader() {
  const top = $('#top'), m = me || {};
  const next = encodeURIComponent(location.pathname + location.search);
  top.replaceChildren(
    h('a', { class: 'brand', href: '/', text: site.title }),
    h('nav', {},
      signinFailed ? h('span', { class: 'chip warn', text: 'Sign-in failed' }) : null,
      m.admin || !m.sso ? h('a', { href: '/admin', text: 'Admin' }) : null,
      m.sso && m.signedIn ? h('span', { class: 'who', text: m.name || 'Signed in' }) : null,
      m.sso ? (m.signedIn ? h('a', { href: '/auth/logout', text: 'Sign out' }) : h('a', { href: '/auth/login?next=' + next, text: 'Sign in' })) : null,
      themePicker()));
}

// On gallery cards the date and the location each get their own line.
const eventLines = (g) => eventParts(g).map(t => h('span', { class: 'muted', text: t }));
function eventLine(g) { return eventParts(g).join(' · '); }
// The date and the place of a gallery as separate pieces (either may be missing).
function eventParts(g) {
  const d = g.eventDate ? new Date(/Z$|[+-]\d\d:\d\d$/.test(g.eventDate) ? g.eventDate : g.eventDate + 'Z').toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' }) : '';
  return [d, g.location].filter(Boolean);
}

// ---------- home ----------
function subscribeForm() {
  const email = h('input', { type: 'email', placeholder: 'Your email address', 'aria-label': 'Email address', maxlength: 200 });
  const msg = h('span', { class: 'muted' });
  const go = async () => {
    const r = await api('/api/subscribe', { method: 'POST', body: { email: email.value } });
    if (r.ok) { email.value = ''; msg.textContent = 'Check your inbox and click the link to confirm.'; }
    else msg.textContent = typeof r.data === 'string' ? r.data : 'Could not subscribe. Try again.';
  };
  return h('form', { class: 'foot-subscribe', onsubmit: (e) => { e.preventDefault(); go(); } },
    h('label', { class: 'foot-subscribe-label', text: 'Email me about new galleries and photos', title: 'Get an email when new galleries and photos are added' }, ),
    email, h('button', { class: 'primary', type: 'submit', text: 'Subscribe' }), msg);
}

// Search: the form is reused on the home page and the results page; the query lives in the address so results can be shared.
function searchForm(v) {
  const q = h('input', { type: 'search', placeholder: 'Search photos, tags, places', value: v.q || '', 'aria-label': 'Search', maxlength: 80 });
  const where = h('input', { type: 'text', placeholder: 'Place or month', value: v.where || '', 'aria-label': 'Place or month', maxlength: 80 });
  const from = h('input', { type: 'date', value: v.from || '', 'aria-label': 'From date' });
  const to = h('input', { type: 'date', value: v.to || '', 'aria-label': 'To date' });
  const tag = h('input', { type: 'text', placeholder: 'Tag', value: v.tag || '', 'aria-label': 'Tag', maxlength: 40 });
  const scopes = [['all', 'Galleries and photos'], ['galleries', 'Galleries only'], ['photos', 'Photos only']];
  const scope = h('select', { 'aria-label': 'Search in' }, scopes.map(([val, text]) => h('option', { value: val, text, selected: val === (v.scope || 'all') })));
  const submit = () => {
    const p = new URLSearchParams();
    if (q.value.trim()) p.set('q', q.value.trim());
    if (where.value.trim()) p.set('where', where.value.trim());
    if (from.value) p.set('from', from.value);
    if (to.value) p.set('to', to.value);
    if (tag.value.trim()) p.set('tag', tag.value.trim());
    if (scope.value !== 'all') p.set('scope', scope.value);
    const qs = p.toString();
    go('/search' + (qs ? '?' + qs : ''));
  };
  // the filters stay folded away until asked for, unless the search already uses one
  const active = [v.where, v.tag, v.from, v.to, v.scope && v.scope !== 'all' ? v.scope : ''].filter(Boolean).length;
  const filters = h('div', { class: 'search-filters', id: 'search-filters' }, scope, where, tag, h('label', { class: 'muted' }, 'From ', from), h('label', { class: 'muted' }, 'To ', to));
  const toggle = h('button', { type: 'button', class: 'filters-toggle', 'aria-controls': 'search-filters' });
  const setOpen = (open) => {
    filters.hidden = !open;
    toggle.setAttribute('aria-expanded', String(open));
    toggle.textContent = (open ? 'Filters \u25B4' : 'Filters \u25BE') + (active ? ` (${active})` : '');
  };
  toggle.onclick = () => setOpen(filters.hidden);
  setOpen(active > 0);
  return h('form', { class: 'search-form', onsubmit: (e) => { e.preventDefault(); submit(); } },
    h('div', { class: 'search-main' }, q, h('button', { class: 'primary', type: 'submit', text: 'Search' }), toggle), filters);
}

async function viewSearch(app) {
  const sp = new URLSearchParams(location.search);
  document.title = 'Search - ' + site.title;
  app.append(h('h1', { text: 'Search' }), searchForm({ q: sp.get('q'), where: sp.get('where'), tag: sp.get('tag'), from: sp.get('from'), to: sp.get('to'), scope: sp.get('scope') }));
  const [r, colors] = await Promise.all([api('/api/search?' + sp.toString()), api('/api/tag-colors')]);
  if (colors.ok) learnTagColors(colors.data);
  if (!r.ok) return app.append(h('p', { class: 'err', text: 'Search failed. Try again.' }));
  const { galleries, photos } = r.data;
  if (!photos.length && !galleries.length) return app.append(h('p', { class: 'muted', text: 'Nothing matched. Only public galleries are searched.' }));
  if (galleries.length) app.append(h('h2', { text: 'Galleries' }), h('div', { class: 'cards' }, galleries.map(g =>
    h('a', { class: 'card', href: '/g/' + encodeURIComponent(g.slug) },
      g.cover ? h('img', { class: 'cover', loading: 'lazy', src: `/img/${g.cover}/thumb`, alt: '' }) : h('div', { class: 'cover' }),
      h('div', { class: 'meta' }, h('b', { text: g.title }), eventLines(g), tagChips(g))))));
  if (photos.length) app.append(h('h2', { text: 'Photos' }), h('div', { class: 'photo-cards' }, photos.map(p =>
    h('a', { class: 'photo-card', href: `/g/${encodeURIComponent(p.slug)}/p/${p.id}` },
      h('img', { loading: 'lazy', src: `/img/${p.id}/thumb`, alt: p.altText || p.name }),
      h('div', { class: 'pc-name', text: p.caption || p.name, title: p.name }),
      h('div', { class: 'muted', text: [p.gallery, p.takenAt ? new Date(p.takenAt + (/Z$/.test(p.takenAt) ? '' : 'Z')).toLocaleDateString() : ''].filter(Boolean).join(' \u00B7 ') }),
      tagLine(p.tags, photoTagEdit(p))))));
}

async function viewHome(app) {
  const [r, colors] = await Promise.all([api('/api/galleries'), api('/api/tag-colors')]);
  if (colors.ok) learnTagColors(colors.data);
  app.append(h('h1', { text: site.title }), h('p', { class: 'sub', text: me && me.admin ? 'All galleries, including ones visitors cannot see' : 'Public galleries' }));
  app.append(searchForm({}));
  if (!r.ok || !r.data.length) return app.append(h('p', { class: 'muted', text: 'No public galleries yet. Private galleries are reached through their share link.' }));
  app.append(h('div', { class: 'cards' }, r.data.map(g =>
    h('a', { class: 'card', href: '/g/' + encodeURIComponent(g.slug) },
      g.cover ? h('img', { class: 'cover', loading: 'lazy', src: `/img/${g.cover}/thumb`, alt: '' }) : h('div', { class: 'cover' }),
      h('div', { class: 'meta' }, h('b', { text: g.title }), eventLines(g), h('span', { class: 'muted', text: plural(g.count, 'photo') }), adminStatus(g, me && me.admin, true), tagChips(g))))));
}

// Admin only: the gallery's visibility level (and whether it has expired) with a shortcut to its settings.
// On a card, which is itself a link, the shortcut is a button; elsewhere it is an ordinary link.
function adminStatus(g, isAdmin, inCard) {
  if (!isAdmin || g.id == null) return null;
  const href = '/admin/galleries/' + g.id;
  const open = inCard
    ? h('button', { type: 'button', class: 'chip-link', text: '\u2699 Settings', title: 'Open this gallery\'s settings', onclick: (e) => { e.preventDefault(); e.stopPropagation(); go(href); } })
    : h('a', { class: 'chip-link', href, text: '\u2699 Gallery settings', title: 'Open this gallery\'s settings' });
  return h('div', { class: 'chips admin-status' },
    h('span', { class: 'chip v' + g.visibility, text: visShort[g.visibility] || 'Unknown', title: visLabels[g.visibility] || '' }),
    g.expired ? h('span', { class: 'chip warn', text: 'Expired' }) : null, open);
}

// ---------- gallery ----------
let G = null;            // current gallery payload
let sel = new Set();     // photo ids chosen for a download
let favs = new Set();    // this visitor's favorite photo ids
const viewKind = () => (innerWidth <= 700 ? 'mid' : 'view');   // phones get the lighter 1280px image
const photoUrl = (p, kind) => `/img/${p.id}/${kind}`;

async function toggleFav(p) {
  const on = !favs.has(p.id);
  on ? favs.add(p.id) : favs.delete(p.id);
  G.refresh && G.refresh();
  const r = await api(`/api/g/${encodeURIComponent(G.slug)}/favorite/${p.id}`, { method: 'POST', body: { on } });
  if (!r.ok) { on ? favs.delete(p.id) : favs.add(p.id); G.refresh && G.refresh(); toast('Could not save that favorite'); return; }
  if (on && favs.size === 1) toast('Saved to your favorites. The photographer can see them.');
}
function playVideo(l) {
  const close = () => { box.remove(); document.removeEventListener('keydown', onKey, true); };
  const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
  const frame = h('iframe', {
    src: `https://www.youtube-nocookie.com/embed/${l.videoId}?autoplay=1&rel=0`, title: l.label, allowfullscreen: true,
    allow: 'autoplay; encrypted-media; picture-in-picture; fullscreen', referrerpolicy: 'strict-origin-when-cross-origin'
  });
  const box = h('div', { class: 'vid-modal', onclick: (e) => { if (e.target === box) close(); } },
    h('div', { class: 'vid-box' }, frame,
      h('div', { class: 'vid-bar' }, h('span', { text: l.label }), h('a', { href: l.url, target: '_blank', rel: 'noopener noreferrer', text: 'Open on YouTube' }), h('button', { text: 'Close', onclick: close }))));
  document.addEventListener('keydown', onKey, true);
  document.body.append(box);
}
function toggleSel(p) { sel.has(p.id) ? sel.delete(p.id) : sel.add(p.id); G.refresh && G.refresh(); }

// A Specific people gallery: visitors must sign in with an address the photographer listed.
function signInPrompt(app, title) {
  document.title = (title || 'Gallery') + ' - ' + site.title;
  const link = me && me.sso ? h('a', { class: 'btn primary', href: '/auth/login?next=' + encodeURIComponent(location.pathname), text: 'Sign in' }) : null;
  app.append(h('div', { class: 'center' }, h('h1', { text: title || 'Private gallery' }),
    h('p', { class: 'sub', text: 'This gallery is only for invited people. Sign in with the email address you were invited on.' }), link));
}

async function viewGallery(app, slug) {
  const r = await api('/api/g/' + encodeURIComponent(slug));
  if (r.status === 401 && r.data && r.data.needsPassword) return passwordPrompt(app, slug, r.data.title);
  if (r.status === 401 && r.data && r.data.needsSignIn) return signInPrompt(app, r.data.title);
  if (r.status === 410) document.title = ((r.data && r.data.title) || 'Gallery') + ' - Closed';
  if (r.status === 410) return app.append(h('div', { class: 'center' }, h('h1', { text: (r.data && r.data.title) || 'Gallery closed' }), h('p', { class: 'sub', text: 'This gallery is no longer available.' })));
  if (!r.ok) document.title = 'Not available - ' + site.title;
  if (!r.ok) return app.append(h('div', { class: 'center' }, h('h1', { text: 'Not available' }), h('p', { class: 'sub', text: 'This gallery does not exist or you do not have access.' })));
  G = r.data;
  learnTagColors(G.tagColors);
  sel = new Set();
  favs = new Set(G.favorites || []);
  document.title = G.title;
  app.append(h('h1', { class: 'g-title', text: G.title }));
  const status = adminStatus(G, G.admin, false);
  if (status) app.append(status);
  if (G.eventDate || G.location) app.append(h('p', { class: 'muted', text: eventLine(G) }));
  const headerTags = tagLine(G.tags, galleryTagEdit(G));
  if (headerTags) app.append(headerTags);
  if (G.description) app.append(h('p', { class: 'sub', text: G.description }));
  const vids = (G.links || []).filter(l => l.category === 'video' && l.videoId), buttons = (G.links || []).filter(l => l.button), external = (G.links || []).filter(l => l.category === 'external');
  if (vids.length) app.append(h('h2', { class: 'vid-head', text: vids.length === 1 ? 'Video' : 'Videos' }), h('div', { class: 'videos' }, vids.map(l =>
    h('button', { class: 'vid', title: l.label, onclick: () => playVideo(l) },
      h('span', { class: 'vid-thumb' }, h('img', { src: `/vthumb/${l.videoId}`, alt: '', loading: 'lazy' }), h('span', { class: 'vid-play', text: '\u25B6' })),
      h('span', { class: 'vid-title', text: l.label }),
      l.note ? h('span', { class: 'vid-note muted', text: l.note }) : null))));
  if (buttons.length) app.append(h('div', { class: 'footage' }, buttons.map(l =>
    h('a', { class: 'btn footage-link', href: l.url, target: '_blank', rel: 'noopener noreferrer', title: l.note || '', text: '▶ ' + l.label }))));
  if (G.link) {
    const bits = [];
    if (G.link.expires) bits.push('link expires ' + fmtDate(G.link.expires));
    if (G.link.remaining !== null && G.link.remaining !== undefined) bits.push(G.link.remaining + ' downloads remaining');
    if (bits.length) app.append(h('p', { class: 'muted', text: bits.join(' · ') }));
  }

  const approved = G.photos.filter(p => p.approved || G.admin);
  const rawOnlyCount = approved.filter(p => p.rawOnly).length;
  let selectMode = false, favOnly = false;
  const tiles = new Map();

  const countLabel = h('span', { class: 'muted' });
  const slideBtn = h('button', { class: 'primary', text: '▶ Slideshow', onclick: () => openLightbox(0, true) });
  const selectBtn = h('button', { text: 'Select', onclick: () => { selectMode = !selectMode; if (!selectMode) sel.clear(); fill(); } });
  const favBtn = h('button', { onclick: () => { favOnly = !favOnly; fill(); } });
  const curated = h('input', { type: 'checkbox', checked: G.hideRawOnly !== false });
  const curatedText = h('span');
  const rawBtn = h('label', { class: 'switch', title: 'Curated photos are the edited ones. Turn this off to also see unedited RAW frames.' }, curated, curatedText);
  const dlBtn = h('button', { text: 'Download…', onclick: () => openDownloadSheet() });
  const grid = h('div', { class: 'grid' });

  // selection bar (bottom of the screen while choosing photos)
  const selCount = h('b');
  const selBar = h('div', { class: 'selbar hidden' }, selCount,
    h('button', { text: 'Select all', onclick: () => { G.list.forEach(p => sel.add(p.id)); G.refresh(); } }),
    h('button', { text: 'Clear', onclick: () => { sel.clear(); G.refresh(); } }),
    h('span', { class: 'grow' }),
    G.admin ? h('button', { text: 'Tag…', onclick: () => bulkTagSheet() }) : null,
    h('button', { class: 'primary', text: 'Download…', onclick: () => openDownloadSheet({ scope: 'selected' }) }),
    h('button', { text: 'Done', onclick: () => { selectMode = false; sel.clear(); fill(); } }));

  // favorites bar
  const nameInput = h('input', { type: 'text', value: G.favoriteName || '', placeholder: 'Your name (optional)', maxlength: 60 });
  nameInput.addEventListener('change', () => api(`/api/g/${encodeURIComponent(slug)}/favorite-name`, { method: 'PUT', body: { name: nameInput.value } }));
  const favBar = h('div', { class: 'panel favbar hidden' },
    h('p', { class: 'muted', text: 'Your favorites are saved in this browser and the photographer can see them. Add your name so they know whose list this is.' }),
    h('div', { class: 'row' }, nameInput, h('button', { text: 'Download favorites…', onclick: () => openDownloadSheet({ scope: 'favorites' }) })));

  const updateStates = () => {
    for (const [id, t] of tiles) {
      t.el.classList.toggle('sel', sel.has(id));
      t.heart.classList.toggle('on', favs.has(id));
      t.heart.textContent = favs.has(id) ? '♥' : '♡';
      t.check.textContent = sel.has(id) ? '✓' : '';
    }
    selCount.textContent = `${sel.size} selected`;
    favBtn.textContent = `♥ Favorites (${favs.size})`;
    favBtn.classList.toggle('on', favOnly);
    if (lb && lb.sync) lb.sync();
  };

  function fill() {
    const showRaw = !curated.checked;
    let photos = showRaw ? approved : approved.filter(p => !p.rawOnly);
    if (favOnly) photos = approved.filter(p => favs.has(p.id));
    G.list = photos;
    countLabel.textContent = favOnly ? `${photos.length} favorites` : plural(photos.filter(p => p.approved).length, 'photo');
    curatedText.textContent = showRaw ? ' Curated only' : ` Curated only (${rawOnlyCount} unedited RAW hidden)`;
    rawBtn.classList.toggle('hidden', favOnly);
    slideBtn.disabled = !photos.length;
    selectBtn.textContent = selectMode ? 'Cancel selection' : 'Select';
    selBar.classList.toggle('hidden', !selectMode);
    favBar.classList.toggle('hidden', !favOnly);
    document.body.classList.toggle('selecting', selectMode);
    tiles.clear();
    grid.replaceChildren(...photos.map((p, i) => {
      const ratio = (p.w || 3) / (p.h || 2);
      const heart = h('button', { class: 'tile-fav', 'aria-label': 'Favorite', onclick: (e) => { e.stopPropagation(); toggleFav(p); } });
      const check = h('span', { class: 'tile-check' });
      const item = h('div', { class: 'ph' + (p.approved ? '' : ' pending'), title: p.name, tabindex: 0,
        onclick: () => { if (selectMode) toggleSel(p); else openLightbox(i, false); },
        onkeydown: (e) => { if (e.key === 'Enter') { e.preventDefault(); if (selectMode) toggleSel(p); else openLightbox(i, false); } } },
        h('img', { loading: 'lazy', src: photoUrl(p, 'thumb'), alt: p.altText || p.name }),
        p.approved ? null : h('span', { class: 'badge', text: 'pending' }),
        p.rawOnly ? h('span', { class: 'badge', text: 'RAW' }) : null,
        selectMode ? check : null, heart);
      item.style.setProperty('--r', String(ratio));
      tiles.set(p.id, { el: item, heart, check });
      return item;
    }));
    updateStates();
  }
  G.refresh = () => { if (favOnly) fill(); else updateStates(); };
  curated.addEventListener('change', fill);

  const tb = h('div', { class: 'toolbar gallery-bar' }, countLabel, slideBtn, selectBtn, favBtn);
  if (G.allowDownload) tb.append(dlBtn);
  if (rawOnlyCount > 0) tb.append(rawBtn);
  tb.append(h('span', { class: 'grow' }));
  if (G.allowUpload) tb.append(uploadControl(slug));
  app.append(tb, favBar, grid, selBar);
  if (external.length) app.append(h('h2', { class: 'ext-head', text: 'Links' }), h('div', { class: 'ext-links' }, external.map(l =>
    h('a', { class: 'ext-card', href: l.url, target: '_blank', rel: 'noopener noreferrer' },
      l.preview && l.preview.image ? h('img', { class: 'ext-img', src: l.preview.image, alt: '', loading: 'lazy' }) : null,
      h('span', { class: 'ext-text' },
        h('span', { class: 'ext-title', text: (l.label === hostOf(l.url) && l.preview && l.preview.title) || l.label }),
        h('span', { class: 'ext-host muted', text: hostOf(l.url) }),
        l.note ? h('span', { class: 'ext-note', text: l.note }) : null)))));
  fill();
  G.revealAll = () => { curated.checked = false; favOnly = false; fill(); };
  galleryView = slug;
}

function hostOf(url) { try { return new URL(url).host.replace(/^www\./, ''); } catch { return ''; } }

function passwordPrompt(app, slug, title) {
  document.title = (title || 'Private gallery') + ' - Password';
  const input = h('input', { type: 'password', placeholder: 'Password', autocomplete: 'current-password' });
  const err = h('div', { class: 'err' });
  const go = async () => {
    const r = await api(`/api/g/${encodeURIComponent(slug)}/unlock`, { method: 'POST', body: { password: input.value } });
    if (r.ok) route(); else err.textContent = r.status === 429 ? 'Too many attempts, wait a minute.' : 'Incorrect password.';
  };
  input.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
  app.append(h('div', { class: 'center' }, h('h1', { text: title || 'Private gallery' }), h('p', { class: 'sub', text: 'This gallery is password protected.' }),
    input, h('button', { class: 'primary', text: 'Open', onclick: go }), err));
  input.focus();
}

// ---------- download sheet ----------
function closeSheet() {
  const s = $('#sheet');
  if (!s) return;
  document.removeEventListener('keydown', s._key, true);
  s.remove();
}

function openDownloadSheet(opts = {}) {
  closeSheet();
  if (!G.allowDownload) return;
  const photo = opts.photo || null;
  const scopes = [];
  if (photo) scopes.push(['photo', `This photo – ${photo.name}`]);
  if (G.allowZip && sel.size) scopes.push(['selected', `Selected photos (${sel.size})`]);
  if (G.allowZip && favs.size) scopes.push(['favorites', `My favorites (${favs.size})`]);
  if (G.allowZip) scopes.push(['all', 'Whole gallery (ZIP)']);
  if (!scopes.length) { toast('Nothing to download'); return; }
  let scope = scopes.some(s => s[0] === opts.scope) ? opts.scope : scopes[0][0];

  const scopeBox = h('div', { class: 'sheet-scopes' });
  const size = h('select'), fmt = h('select'), rawBox = h('input', { type: 'checkbox' });
  const rawRow = h('label', { class: 'sheet-row' }, rawBox, ' Include the RAW files (where they exist)');
  const note = h('p', { class: 'muted' });
  const needAgree = license.enabled && license.require && !license.accepted;
  const agreeBox = h('input', { type: 'checkbox' });
  const agreeRow = needAgree ? h('div', { class: 'license-agree' },
    h('label', { class: 'sheet-row' }, agreeBox, h('span', {}, ' I have read and agree to the ', h('a', { href: '/license', target: '_blank', rel: 'noopener', text: license.title || 'license agreement' }), ': personal, non-commercial use only.'))) : null;
  const agreed = () => !needAgree || agreeBox.checked;
  const rawBtn = photo && photo.raw && G.allowRaw ? h('a', { class: 'btn', href: `/dl/${photo.id}/raw`, text: 'Download the RAW file',
    onclick: async (e) => {
      e.preventDefault();
      if (!agreed()) { toast('Please agree to the license first'); return; }
      if (needAgree && !(await recordLicenseAcceptance())) return;
      location.href = `/dl/${photo.id}/raw`;
    } }) : null;

  const fillSizes = () => {
    const keep = size.value;
    size.replaceChildren();
    if (G.allowOriginal && !(scope === 'photo' && photo.rawOnly)) size.append(h('option', { value: 'original', text: 'Original file (untouched)' }));
    for (const px of G.sizes) size.append(h('option', { value: px, text: `${px}px on the long edge` }));
    if ([...size.options].some(o => o.value === keep)) size.value = keep;
    fmt.disabled = size.value === 'original';
  };
  for (const f of G.formats) fmt.append(h('option', { value: f, text: fmtLabels[f] || f }));
  const sync = () => {
    fillSizes();
    rawRow.classList.toggle('hidden', !(G.allowRaw && scope !== 'photo'));
    note.textContent = (G.watermarkDownload ? 'Downloads carry a watermark, so the original file is not offered. ' : '') + 'Resized copies are lossless.';
  };
  scopeBox.replaceChildren(...scopes.map(([v, t]) => h('label', { class: 'sheet-row' },
    h('input', { type: 'radio', name: 'scope', value: v, checked: v === scope, onchange: () => { scope = v; sync(); } }), ' ' + t)));
  size.addEventListener('change', () => { fmt.disabled = size.value === 'original'; });
  sync();

  const go = async () => {
    if (!agreed()) { toast('Please agree to the license first'); return; }
    if (needAgree && !(await recordLicenseAcceptance())) return;
    const q = new URLSearchParams({ size: size.value, fmt: fmt.value });
    if (scope === 'photo') { location.href = `/dl/${photo.id}?${q}`; closeSheet(); return; }
    if (rawBox.checked && G.allowRaw) q.set('raw', 'true');
    if (scope !== 'all') {
      const ids = scope === 'selected' ? [...sel] : [...favs];
      if (ids.length > 500) { toast('Choose 500 photos or fewer at a time'); return; }
      q.set('ids', ids.join(','));
    }
    location.href = `/api/g/${encodeURIComponent(G.slug)}/zip?${q}`;
    closeSheet();
    toast('Your download is starting…');
  };

  const dlGo = h('button', { class: 'primary', text: 'Download', onclick: go });
  const sheet = h('div', { id: 'sheet', class: 'sheet-backdrop', onclick: (e) => { if (e.target.id === 'sheet') closeSheet(); } },
    h('div', { class: 'sheet', role: 'dialog', 'aria-label': 'Download' },
      h('div', { class: 'sheet-grab' }),
      h('h3', { text: 'Download' }),
      scopes.length > 1 ? scopeBox : h('p', { text: scopes[0][1] }),
      h('div', { class: 'sheet-fields' }, h('label', {}, 'Size', size), h('label', {}, 'Format', fmt)),
      rawRow, note, agreeRow,
      license.enabled && !needAgree ? h('p', { class: 'muted' }, 'Downloads are for personal, non-commercial use. See the ', h('a', { href: '/license', target: '_blank', rel: 'noopener', text: license.title || 'license' }), '.') : null,
      G.link && G.link.remaining !== null && G.link.remaining !== undefined ? h('p', { class: 'muted', text: `${G.link.remaining} downloads remaining on your link` }) : null,
      h('div', { class: 'row sheet-actions' }, dlGo, rawBtn, h('button', { text: 'Close', onclick: closeSheet }))));
  const syncGo = () => { dlGo.disabled = !agreed(); };
  agreeBox.addEventListener('change', syncGo); syncGo();
  sheet._key = (e) => { if (e.key === 'Escape') { e.stopImmediatePropagation(); e.preventDefault(); closeSheet(); } };
  document.addEventListener('keydown', sheet._key, true);
  document.body.append(sheet);
}

// Details of a photo: each file behind it (the image and its RAW, if any) with the metadata that file carries.
// Returns the nodes for the panel that sits beside the image in the viewer, or null when they cannot be loaded.
async function photoDetails(photo) {
  const r = await api(`/api/g/${encodeURIComponent(G.slug)}/photo/${photo.id}/info`);
  if (!r.ok) return null;
  const d = r.data;
  const table = (fields) => h('table', { class: 'info-table' }, Object.entries(fields).map(([k, v]) => h('tr', {}, h('th', { text: k }), h('td', { text: String(v) }))));
  const version = (v) => h('div', { class: 'info-version' }, h('h4', { text: `${v.role}: ${v.file}` }),
    Object.entries(v.groups).map(([g, fields]) => h('details', { open: g === 'File' || g === 'EXIF' }, h('summary', { text: g }), table(fields))));
  return [captionBlock(photo),
    ...(d.versions.length ? d.versions.map(version) : [h('p', { class: 'muted', text: 'No details could be read for this photo.' })]),
    h('p', { class: 'muted', text: 'Where the photo was taken is not shown to visitors.' })];
}

// Gallery tags as a line of #labels (empty when the gallery has none).
function tagChips(g) {
  return tagLine(g.tags, galleryTagEdit(g));
}

// Tag chips with a + button. Typing offers the tags already on the site; Enter or a comma adds the typed tag,
// Backspace on an empty box removes the last chip. names() returns the current list.
function tagEditor(initial, onChange, opts = {}) {
  let names = [...new Set(initial)];
  let known = [];
  const clean = (s) => String(s || '').trim().replace(/^#+/, '').replace(/\s+/g, ' ').slice(0, 40).trim();
  const has = (n) => names.some(x => x.toLowerCase() === n.toLowerCase());
  const chips = h('div', { class: 'tag-chips' });
  const addBox = h('input', { type: 'text', maxlength: 40, placeholder: 'Type a tag', 'aria-label': 'New tag', autocomplete: 'off' });
  const list = h('div', { class: 'tag-suggest', role: 'listbox' });
  const addRow = h('div', { class: 'tag-add' }, addBox, list);
  addRow.hidden = true;
  const draw = () => {
    chips.replaceChildren(...names.map(n => {
      const chip = h('span', { class: 'tag-chip' }, '#' + n,
        h('button', { type: 'button', class: 'tag-x', 'aria-label': 'Remove ' + n, text: '×', onclick: () => { names = names.filter(x => x !== n); draw(); emit(); } }));
      chip.style.setProperty('--tag-c', tagColor(n));   // the page's CSP forbids style attributes, so set it through the style object
      return chip;
    }));
  };
  const emit = () => { if (onChange) onChange(); };
  const suggest = () => {
    const q = clean(addBox.value).toLowerCase();
    const pool = [...known].sort((a, b) => (b.photos + b.galleries) - (a.photos + a.galleries) || a.name.localeCompare(b.name)).map(t => t.name).filter(n => !has(n) && (!q || n.toLowerCase().includes(q)));
    list.replaceChildren(...pool.slice(0, 8).map(n => h('button', { type: 'button', class: 'tag-option', text: n,
      // mousedown keeps focus in the box so the list stays open until the tag is picked
      onmousedown: (e) => { e.preventDefault(); add(n); } })));
    list.hidden = pool.length === 0;
  };
  const add = (raw) => {
    const name = clean(raw);
    if (name && !has(name)) {
      names.push(known.find(t => t.name.toLowerCase() === name.toLowerCase())?.name || name);
      draw(); emit();
    }
    addBox.value = '';
    suggest();
    addBox.focus();
  };
  const closeAdd = () => { addRow.hidden = true; addBox.value = ''; };
  const openAdd = async () => {
    addRow.hidden = false;
    addBox.focus();
    const r = await api('/api/admin/tags');
    if (r.ok && Array.isArray(r.data)) { known = r.data; for (const t of known) if (t.color) tagColorMap[t.name.toLowerCase()] = t.color; draw(); }
    suggest();
  };
  addBox.addEventListener('input', suggest);
  addBox.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); add(addBox.value); }
    else if (e.key === 'Backspace' && !addBox.value && names.length) { names.pop(); draw(); emit(); }
  });
  addBox.addEventListener('blur', () => setTimeout(() => { if (!opts.open && document.activeElement !== addBox) closeAdd(); }, 150));
  draw();
  const wrap = h('div', { class: 'tag-editor' + (opts.open ? ' open' : '') }, chips,
    opts.open ? null : h('button', { type: 'button', class: 'tag-plus', title: 'Add a tag', 'aria-label': 'Add a tag', text: '+', onclick: openAdd }), addRow);
  if (opts.open) openAdd();
  wrap.names = () => {
    const pending = clean(addBox.value);
    return pending && !has(pending) ? [...names, pending] : [...names];
  };
  return wrap;
}

function captionBlock(photo) {
  return h('div', { class: 'info-caption' }, h('h4', { text: 'Caption and description' }),
    h('p', { text: photo.caption || 'No caption yet.' }),
    h('p', { class: 'muted', text: photo.altText ? `Alt text: ${photo.altText}` : 'No alt text yet.' }),
    h('div', {}, h('span', { class: 'muted', text: 'Tags' }), tagLine(photo.tags, { ...photoTagEdit(photo), onSaved: (t) => { photo.tags = t; if (lb && lb.drawCaption) lb.drawCaption(); } }) || h('span', { class: 'muted', text: ': none yet.' })),
    h('button', { text: me && me.admin ? 'Edit' : 'Suggest a change', onclick: () => captionSheet(photo) }));
}

// Edit (the photographer) or suggest (a signed-in visitor, who the photographer then reviews like a merge).
function captionSheet(photo) {
  closeSheet();
  const isAdmin = !!(me && me.admin), signedIn = !!(me && me.signedIn);
  const caption = h('input', { type: 'text', maxlength: 120, value: photo.caption || '', 'aria-label': 'Caption' });
  const alt = h('textarea', { rows: 3, maxlength: 300, 'aria-label': 'Alt text' });
  alt.value = photo.altText || '';
  const tags = isAdmin ? tagEditor(photo.tags || []) : null;
  const msg = h('span', { class: 'muted' });
  const note = isAdmin ? 'Your changes apply straight away.'
    : signedIn ? 'Your suggestion goes to the photographer, who approves it before it appears.'
    : me && me.sso ? 'Sign in to suggest a change.' : 'Only the photographer can change this.';
  const submit = async () => {
    const r = await api(`/api/g/${encodeURIComponent(G.slug)}/photo/${photo.id}/suggest`, { method: 'POST', body: { caption: caption.value, alt: alt.value } });
    if (r.status === 401) { msg.textContent = 'Sign in to suggest a change.'; return; }
    if (!r.ok) { msg.textContent = 'Could not save. Try again.'; return; }
    if (tags) { const tr = await api(`/api/admin/photos/${photo.id}/tags`, { method: 'PUT', body: { names: tags.names() } }); if (tr.ok) photo.tags = tr.data; }
    if (r.data && r.data.applied) { photo.caption = r.data.caption; photo.altText = r.data.alt; closeSheet(); toast('Saved'); return; }
    msg.textContent = r.data && r.data.suggested ? 'Thanks. The photographer will review it.' : 'Nothing new to suggest.';
  };
  const sheet = h('div', { id: 'sheet', class: 'sheet-backdrop', onclick: (e) => { if (e.target.id === 'sheet') closeSheet(); } },
    h('div', { class: 'sheet', role: 'dialog', 'aria-label': 'Caption' },
      h('div', { class: 'sheet-grab' }), h('h3', { text: isAdmin ? 'Edit caption' : 'Suggest a caption' }),
      h('label', { class: 'sheet-field' }, 'Caption', caption),
      h('label', { class: 'sheet-field' }, 'Alt text (describes the photo for screen readers)', alt),
      tags ? h('div', { class: 'sheet-field' }, h('span', { text: 'Tags' }), tags) : null,
      h('p', { class: 'muted', text: note }), msg,
      h('div', { class: 'row sheet-actions' }, h('button', { class: 'primary', text: isAdmin ? 'Save' : 'Send suggestion', onclick: submit }), h('button', { text: 'Close', onclick: closeSheet }))));
  sheet._key = (e) => { if (e.key === 'Escape') { e.stopImmediatePropagation(); e.preventDefault(); closeSheet(); } };
  document.addEventListener('keydown', sheet._key, true);
  document.body.append(sheet);
}

// Admin: add tags to, or take tags off, every selected photo at once. Other tags on those photos are left alone.
function bulkTagSheet() {
  closeSheet();
  const ids = [...sel];
  if (!ids.length) { toast('Tick some photos first'); return; }
  const add = tagEditor([], null, { open: true });
  const remove = tagEditor([], null, { open: true });
  const msg = h('span', { class: 'muted' });
  const submit = async () => {
    const a = add.names(), r = remove.names();
    if (!a.length && !r.length) { msg.textContent = 'Pick a tag to add or remove.'; return; }
    const res = await api('/api/admin/photos/tags', { method: 'POST', body: { photoIds: ids, add: a, remove: r } });
    if (!res.ok) { msg.textContent = 'Could not save. Try again.'; return; }
    const lower = (x) => x.toLowerCase();
    for (const p of G.photos) if (ids.includes(p.id)) {
      const cur = (p.tags || []).filter(t => !r.some(x => lower(x) === lower(t)));
      for (const t of a) if (!cur.some(x => lower(x) === lower(t))) cur.push(t);
      p.tags = cur.sort();
    }
    closeSheet(); toast(`Tags updated on ${plural(res.data.photos, 'photo')}`);
  };
  const sheet = h('div', { id: 'sheet', class: 'sheet-backdrop', onclick: (e) => { if (e.target.id === 'sheet') closeSheet(); } },
    h('div', { class: 'sheet', role: 'dialog', 'aria-label': 'Tag photos' },
      h('div', { class: 'sheet-grab' }), h('h3', { text: `Tag ${plural(ids.length, 'photo')}` }),
      h('div', { class: 'sheet-field' }, h('span', { text: 'Add these tags' }), add),
      h('div', { class: 'sheet-field' }, h('span', { text: 'Remove these tags' }), remove),
      h('p', { class: 'muted', text: 'Type a tag and press Enter. A new name creates the tag. Other tags on the photos stay as they are.' }), msg,
      h('div', { class: 'row sheet-actions' }, h('button', { class: 'primary', text: 'Apply', onclick: submit }), h('button', { text: 'Close', onclick: closeSheet }))));
  sheet._key = (e) => { if (e.key === 'Escape') { e.stopImmediatePropagation(); e.preventDefault(); closeSheet(); } };
  document.addEventListener('keydown', sheet._key, true);
  document.body.append(sheet);
}

function uploadControl(slug) {
  const file = h('input', { type: 'file', accept: 'image/*,.cr2,.cr3,.nef,.arw,.dng,.raf,.orf,.rw2', multiple: true, class: 'hidden' });
  const label = h('span', { class: 'muted' });
  file.addEventListener('change', async () => {
    let ok = 0, fail = 0;
    for (const f of file.files) {
      label.textContent = `Uploading ${ok + fail + 1}/${file.files.length}...`;
      const fd = new FormData(); fd.append('file', f);
      const r = await api(`/api/g/${encodeURIComponent(slug)}/upload`, { method: 'POST', form: fd });
      if (r.ok) ok++; else { fail++; toast(`${f.name}: ${typeof r.data === 'string' ? r.data : 'failed (' + r.status + ')'}`); }
    }
    label.textContent = `${ok} uploaded${fail ? ', ' + fail + ' failed' : ''}${G.uploadsModerated && ok ? ' (awaiting approval)' : ''}`;
    file.value = '';
    if (ok && !G.uploadsModerated) route(true);
  });
  return h('div', { class: 'dlbar' }, h('button', { text: 'Upload photos', onclick: () => file.click() }), file, label);
}

// ---------- lightbox / slideshow ----------
// Every photo has its own address (/g/<slug>/p/<id>). Opening the viewer adds one history entry, moving between
// photos only replaces the address, so Back (or the mouse Back button) returns straight to the grid.
const gridPath = () => '/g/' + encodeURIComponent(G.slug);
const photoPath = (p) => gridPath() + '/p/' + p.id;
const photoTitle = (p) => `${p.name}${G.photos.filter(x => x.name === p.name).length > 1 ? ` (#${p.id})` : ''} - ${G.title}`;
function syncViewer(pid, mode = 'sync') {
  if (!pid) { closeLightbox(); return; }
  let i = G.list.findIndex(p => p.id === pid);
  if (i < 0 && G.revealAll) { G.revealAll(); i = G.list.findIndex(p => p.id === pid); }
  if (i < 0) { history.replaceState(null, '', gridPath()); closeLightbox(); return; }
  if (lb) lb.show(i, false); else openLightbox(i, false, mode);
}
function requestClose() {
  if (!lb) return;
  if (history.state && history.state.viewer) history.back();      // popstate closes the viewer
  else { history.replaceState(null, '', gridPath()); closeLightbox(); }
}
let lb = null;
function openLightbox(index, play, mode = 'push') {
  const list = G.list; if (!list.length) return;
  if (mode === 'push') history.pushState({ viewer: 1 }, '', photoPath(list[index]));
  const root = $('#lightbox'); root.classList.remove('hidden', 'idle', 'chrome-hidden'); root.replaceChildren();
  document.body.style.overflow = 'hidden';

  const act = (icon, label) => { const i = h('span', { class: 'ic', text: icon }), l = h('span', { class: 'lbl', text: label }); return { el: h('button', { class: 'lb-act' }, i, l), i, l }; };
  const img = h('img', { alt: '', draggable: 'false' });
  const title = h('div', { class: 'lb-title' });
  const captionEl = h('div', { class: 'lb-caption hidden' });
  const counter = h('div', { class: 'lb-count' });
  const closeBtn = h('button', { class: 'lb-close', 'aria-label': 'Close', text: '✕' });
  const prev = h('button', { class: 'nav prev', text: '‹', 'aria-label': 'Previous' });
  const next = h('button', { class: 'nav next', text: '›', 'aria-label': 'Next' });
  const favA = act('♡', 'Favorite'), selA = act('☐', 'Select'), dlA = act('⬇', 'Download'), playA = act('▶', 'Play'), infoA = act('ⓘ', 'Details');
  const speed = h('select', { 'aria-label': 'Slide time' }, [3, 5, 8, 12].map(s => h('option', { value: s, text: s + 's', selected: s === 5 })));
  const fsBtn = h('button', { class: 'lb-small', text: 'Fullscreen' });
  const adminRow = h('div', { class: 'lb-admin' });
  const stage = h('div', { class: 'stage' }, prev, img, next);
  const details = h('aside', { class: 'lb-details hidden', 'aria-label': 'Photo details' });
  const actions = h('div', { class: 'lb-actions' }, favA.el, selA.el);
  if (G.allowDownload) actions.append(dlA.el);
  actions.append(infoA.el);
  actions.append(playA.el);
  root.append(
    h('div', { class: 'lb-top' }, h('div', { class: 'lb-info' }, title, counter), closeBtn),
    h('div', { class: 'lb-main' }, stage, details),
    h('div', { class: 'lb-bottom' }, captionEl, actions, h('div', { class: 'lb-extra' }, speed, fsBtn), adminRow));
  lb = { i: index, timer: null, idle: null, playing: false, lastSeen: null };

  // zoom / pan state of the current photo
  const z = { s: 1, x: 0, y: 0, hi: false };
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const apply = (animate) => { img.style.transition = animate ? 'transform .2s ease' : 'none'; img.style.transform = `translate(${z.x}px, ${z.y}px) scale(${z.s})`; };
  const clampPan = () => {
    const mx = Math.max(0, (img.clientWidth * z.s - stage.clientWidth) / 2), my = Math.max(0, (img.clientHeight * z.s - stage.clientHeight) / 2);
    z.x = clamp(z.x, -mx, mx); z.y = clamp(z.y, -my, my);
  };
  const resetZoom = () => { z.s = 1; z.x = z.y = 0; z.hi = false; apply(false); };
  const cur = () => list[lb.i];
  const zoomAt = (ns, cx, cy, animate) => {
    ns = clamp(ns, 1, 6);
    const k = ns / z.s;
    z.x = cx - (cx - z.x) * k; z.y = cy - (cy - z.y) * k; z.s = ns;
    if (z.s <= 1.02) { z.s = 1; z.x = z.y = 0; }
    clampPan(); apply(animate);
    if (z.s > 1.6 && !z.hi && viewKind() === 'mid') { z.hi = true; img.src = photoUrl(cur(), 'view'); }   // sharper image once zoomed in
  };
  const centre = (e) => { const r = stage.getBoundingClientRect(); return [e.clientX - r.left - r.width / 2, e.clientY - r.top - r.height / 2]; };

  // the caption and tags under the photo; the admin also gets a + to add tags without leaving the viewer
  lb.drawCaption = () => {
    const p = cur(); if (!p) return;
    const tags = tagLine(p.tags, photoTagEdit(p));
    captionEl.replaceChildren(...[p.caption ? h('div', { text: p.caption }) : null, tags].filter(Boolean));
    captionEl.classList.toggle('hidden', !p.caption && !tags);
  };
  const show = (i, updateUrl = true) => {
    lb.i = (i + list.length) % list.length;
    const p = cur();
    if (updateUrl) history.replaceState(history.state, '', photoPath(p));
    resetZoom();
    img.src = photoUrl(p, viewKind());
    if (p.id !== lb.lastSeen) { lb.lastSeen = p.id; fetch(`/api/g/${encodeURIComponent(G.slug)}/viewed/${p.id}`, { method: 'POST', headers: { 'X-Requested-With': 'pg' }, credentials: 'same-origin', keepalive: true }).catch(() => {}); }
    title.textContent = p.name;
    lb.drawCaption(); img.alt = p.altText || p.name;
    document.title = photoTitle(p); counter.textContent = `${lb.i + 1} / ${list.length}`;
    lb.sync(); lb.drawDetails();
    const n = list[(lb.i + 1) % list.length]; new Image().src = photoUrl(n, viewKind());   // preload
  };
  lb.sync = () => {
    const p = cur(); if (!p) return;
    const fav = favs.has(p.id), chosen = sel.has(p.id);
    favA.i.textContent = fav ? '♥' : '♡'; favA.l.textContent = fav ? 'Favorited' : 'Favorite'; favA.el.classList.toggle('on', fav);
    selA.i.textContent = chosen ? '☑' : '☐'; selA.l.textContent = chosen ? 'Selected' : 'Select'; selA.el.classList.toggle('on', chosen);
    const unlink = G.admin && p.raw && !p.rawOnly
      ? h('button', { class: 'danger', text: 'Unlink RAW', onclick: async () => { if (confirm('Unlink the RAW from this photo?')) { await api(`/api/admin/photos/${p.id}/link-raw`, { method: 'DELETE' }); route(true); } } })
      : null;
    const coverBtn = G.admin && p.approved
      ? h('button', { text: G.cover === p.id ? '★ Gallery cover' : 'Set as gallery cover', disabled: G.cover === p.id, onclick: async () => {
          const r = await api(`/api/admin/galleries/${G.id}/cover`, { method: 'PUT', body: { photoId: p.id } });
          if (r.ok) { G.cover = p.id; toast('Gallery cover updated'); lb.sync(); } else toast('Could not set cover');
        } })
      : null;
    adminRow.replaceChildren(...[adminStatus(G, G.admin, false), coverBtn, unlink].filter(Boolean));
  };
  favA.el.onclick = () => toggleFav(cur());
  selA.el.onclick = () => toggleSel(cur());
  dlA.el.onclick = () => openDownloadSheet({ photo: cur() });
  // The details open in a panel beside the photo (under it on a phone) and follow the photo as you move through the gallery.
  const setDetails = (open) => {
    details.classList.toggle('hidden', !open); root.classList.toggle('has-details', open); infoA.el.classList.toggle('open', open);
    resetZoom(); lb.drawDetails();
  };
  lb.drawDetails = async () => {
    const p = cur();
    if (!p || details.classList.contains('hidden')) return;
    const head = h('div', { class: 'lb-details-head' }, h('h3', { text: p.name }), h('button', { text: 'Close', onclick: () => setDetails(false) }));
    details.replaceChildren(head, h('p', { class: 'muted', text: 'Loading...' }));
    const body = await photoDetails(p);
    if (!lb || cur() !== p || details.classList.contains('hidden')) return;   // moved on while it loaded
    details.replaceChildren(head, ...(body || [h('p', { class: 'muted', text: 'Details are not available for this photo.' })]));
    details.scrollTop = 0;
  };
  infoA.el.onclick = () => setDetails(details.classList.contains('hidden'));

  const stop = () => { lb.playing = false; clearInterval(lb.timer); playA.i.textContent = '▶'; playA.l.textContent = 'Play'; };
  const start = () => { lb.playing = true; playA.i.textContent = '❚❚'; playA.l.textContent = 'Pause'; clearInterval(lb.timer); lb.timer = setInterval(() => show(lb.i + 1), Number(speed.value) * 1000); };
  playA.el.onclick = () => lb.playing ? stop() : start();
  speed.onchange = () => { if (lb.playing) start(); };
  prev.onclick = () => show(lb.i - 1); next.onclick = () => show(lb.i + 1);
  lb.show = show;
  closeBtn.onclick = requestClose;
  fsBtn.onclick = () => document.fullscreenElement ? document.exitFullscreen() : root.requestFullscreen?.();
  const wake = () => { root.classList.remove('idle'); clearTimeout(lb.idle); lb.idle = setTimeout(() => lb.playing && root.classList.add('idle'), 3000); };
  root.onmousemove = wake;
  lb.key = (e) => {
    if (!lb) return;
    if (e.target.tagName === 'SELECT' || e.target.tagName === 'INPUT') return;
    if (e.key === 'Escape') requestClose();
    else if (e.key === 'ArrowRight') show(lb.i + 1);
    else if (e.key === 'ArrowLeft') show(lb.i - 1);
    else if (e.key === ' ') { e.preventDefault(); lb.playing ? stop() : start(); }
    else if (e.key === 'f') fsBtn.click();
    else if (e.key === 'h') toggleFav(cur());
    else if (e.key === '+' || e.key === '=') zoomAt(z.s * 1.3, 0, 0, true);
    else if (e.key === '-') zoomAt(z.s / 1.3, 0, 0, true);
    else if (e.key === '0') resetZoom();
    wake();
  };
  document.addEventListener('keydown', lb.key);

  // --- touch, pen and mouse gestures: swipe, pinch, double-tap, drag-to-pan, wheel zoom ---
  const ptrs = new Map();
  let g = null, lastTap = 0, tapTimer = null;
  const dist = () => { const [a, b] = [...ptrs.values()]; return Math.hypot(a.x - b.x, a.y - b.y); };
  stage.addEventListener('pointerdown', (e) => {
    if (e.target.closest('.nav')) return;
    try { stage.setPointerCapture(e.pointerId); } catch { /* synthetic or already released */ }
    ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY, sx: e.clientX, sy: e.clientY, type: e.pointerType });
    if (ptrs.size === 2) g = { kind: 'pinch', d: dist(), moved: true };
    else if (ptrs.size === 1) g = { kind: 'drag', ox: z.x, oy: z.y, moved: false, t: Date.now() };
  });
  stage.addEventListener('pointermove', (e) => {
    const p = ptrs.get(e.pointerId); if (!p || !g) return;
    p.x = e.clientX; p.y = e.clientY;
    if (g.kind === 'pinch' && ptrs.size === 2) {
      const d = dist(), [a, b] = [...ptrs.values()], r = stage.getBoundingClientRect();
      zoomAt(z.s * d / g.d, (a.x + b.x) / 2 - r.left - r.width / 2, (a.y + b.y) / 2 - r.top - r.height / 2, false);
      g.d = d;
    } else if (g.kind === 'drag' && ptrs.size === 1) {
      const dx = p.x - p.sx, dy = p.y - p.sy;
      if (Math.abs(dx) + Math.abs(dy) > 8) g.moved = true;
      if (z.s > 1) { z.x = g.ox + dx; z.y = g.oy + dy; clampPan(); apply(false); }
      else if (g.moved && p.type !== 'mouse') { img.style.transition = 'none'; img.style.transform = `translate(${dx}px, ${Math.max(0, dy) * 0.6}px)`; g.dx = dx; g.dy = dy; }
    }
  });
  const end = (e) => {
    const p = ptrs.get(e.pointerId); if (!p) return;
    ptrs.delete(e.pointerId);
    if (!g) return;
    if (g.kind === 'pinch') { if (ptrs.size < 2) g = ptrs.size === 1 ? { kind: 'drag', ox: z.x, oy: z.y, moved: true } : null; if (g && ptrs.size === 1) { const q = [...ptrs.values()][0]; q.sx = q.x; q.sy = q.y; } return; }
    if (g.kind === 'drag') {
      const moved = g.moved, dx = g.dx || 0, dy = g.dy || 0; g = null;
      if (!moved) {                                    // a tap
        if (p.type === 'mouse') return;
        const now = Date.now();
        if (now - lastTap < 300) { clearTimeout(tapTimer); lastTap = 0; const [cx, cy] = centre(e); zoomAt(z.s > 1 ? 1 : 2.5, cx, cy, true); }
        else { lastTap = now; tapTimer = setTimeout(() => root.classList.toggle('chrome-hidden'), 280); }
        return;
      }
      if (p.type === 'mouse') { apply(true); return; }
      if (z.s === 1) {
        if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.3) show(lb.i + (dx < 0 ? 1 : -1));
        else if (dy > 110 && dy > Math.abs(dx) * 1.3) requestClose();
        else apply(true);
      }
    }
  };
  stage.addEventListener('pointerup', end);
  stage.addEventListener('pointercancel', (e) => { ptrs.delete(e.pointerId); g = null; apply(true); });
  stage.addEventListener('dblclick', (e) => { if (e.pointerType === 'touch') return; const [cx, cy] = centre(e); zoomAt(z.s > 1 ? 1 : 2.5, cx, cy, true); });
  stage.addEventListener('wheel', (e) => { e.preventDefault(); const [cx, cy] = centre(e); zoomAt(z.s * (e.deltaY < 0 ? 1.18 : 1 / 1.18), cx, cy, false); }, { passive: false });

  show(index, false); if (play) { start(); } wake();
}
function closeLightbox() {
  const root = $('#lightbox');
  if (!lb) return;
  clearInterval(lb.timer); clearTimeout(lb.idle);
  document.removeEventListener('keydown', lb.key);
  if (document.fullscreenElement) document.exitFullscreen();
  closeSheet();
  root.classList.add('hidden'); root.replaceChildren(); document.body.style.overflow = ''; lb = null;
  if (galleryView && G) document.title = G.title;
}

// ---------- admin ----------
// Every admin page is drawn inside a shell: a menu on the left, the page in the middle.
const adminNav = [
  { href: '/admin', key: 'overview', label: 'Overview' },
  { href: '/admin/galleries', key: 'galleries', label: 'Galleries' },
  { href: '/admin/stats', key: 'stats', label: 'Statistics' },
  { heading: 'Settings' },
  { href: '/admin/settings', key: 'settings/general', label: 'Site and appearance' },
  { href: '/admin/settings/watermark', key: 'settings/watermark', label: 'Watermark' },
  { href: '/admin/settings/review', key: 'settings/review', label: 'Caption suggestions' },
  { href: '/admin/settings/tags', key: 'settings/tags', label: 'Tags' },
  { href: '/admin/settings/groups', key: 'settings/groups', label: 'Groups' },
  { href: '/admin/settings/license', key: 'settings/license', label: 'License' },
  { href: '/admin/settings/visitor-stats', key: 'settings/visitor-stats', label: 'Visitor tracking' },
  { href: '/admin/settings/email', key: 'settings/email', label: 'Email alerts' },
  { href: '/admin/settings/cache', key: 'settings/cache', label: 'Thumbnail cache' }];

// Signs in the admin if needed, then draws the shell and returns the middle column (null when the sign-in page was shown instead).
async function adminPage(app, key, title) {
  const meRes = await api('/api/admin/me');
  if (!meRes.data || !meRes.data.admin) { await adminLogin(app); return null; }
  if (me) me.admin = true;
  const nav = h('nav', { class: 'admin-nav', 'aria-label': 'Admin' },
    adminNav.map(n => n.heading ? h('div', { class: 'nav-heading', text: n.heading })
      : h('a', { href: n.href, class: n.key === key ? 'on' : '', 'aria-current': n.key === key ? 'page' : false, text: n.label })),
    h('div', { class: 'nav-foot' },
      h('a', { href: '/', text: 'View site' }),
      h('button', { text: 'Log out', onclick: async () => { await api('/api/admin/logout', { method: 'POST' }); route(); } })));
  const main = h('div', { class: 'admin-main' }, title ? h('h1', { text: title }) : null);
  app.append(h('div', { class: 'admin-shell' }, nav, main));
  return main;
}

const healthHeadline = { ok: 'Everything looks healthy', warn: 'A few things need your attention', bad: 'Something is wrong and needs action' };
const fmtUptime = (s) => s >= 86400 ? plural(Math.floor(s / 86400), 'day') : s >= 3600 ? plural(Math.floor(s / 3600), 'hour') : plural(Math.max(1, Math.floor(s / 60)), 'minute');

// The landing page: a quick read on whether the app is healthy. Detail lives under Statistics and Settings.
async function viewOverview(app) {
  document.title = 'Admin - ' + site.title;
  const main = await adminPage(app, 'overview', 'Overview');
  if (!main) return;
  const body = h('div');
  main.append(body);
  let timer;
  const load = async () => {
    const r = await api('/api/admin/health');
    if (!body.isConnected) return;
    if (!r.ok) { body.replaceChildren(h('p', { class: 'err', text: 'Could not load the health checks.' })); return; }
    const d = r.data;
    const attention = d.checks.filter(c => c.status === 'warn' || c.status === 'bad').length;
    const tile = (n, label) => h('div', { class: 'tile' }, h('div', { class: 'tile-n', text: String(n) }), h('div', { class: 'muted', text: label }));
    const groups = [...new Set(d.checks.map(c => c.group))];
    body.replaceChildren(
      h('div', { class: 'health-banner ' + d.overall },
        h('span', { class: 'dot big ' + d.overall }),
        h('div', {}, h('strong', { text: healthHeadline[d.overall] }),
          h('div', { class: 'muted', text: attention ? `${plural(attention, 'check')} to look at, ${d.checks.length - attention} fine.` : `All ${d.checks.length} checks passed.` })),
        h('span', { class: 'grow' }),
        h('button', { text: 'Refresh', onclick: load })),
      h('div', { class: 'tiles' }, tile(d.counts.galleries, 'Galleries'), tile(d.counts.photos, 'Photos'), tile(d.counts.subscribers, 'Subscribers'),
        tile(fmtUptime(d.uptimeSeconds), 'Running for'), tile('v' + d.version, d.commit && d.commit !== 'dev' ? d.commit : 'Development build')),
      ...groups.map(grp => h('div', {}, h('h2', { text: grp }),
        h('div', { class: 'checks' }, d.checks.filter(c => c.group === grp).map(c => h('div', { class: 'check ' + c.status },
          h('div', { class: 'check-head' }, h('span', { class: 'dot ' + c.status }), h('strong', { text: c.label }), h('span', { class: 'grow' }), h('span', { class: 'check-val ' + c.status, text: c.value })),
          h('div', { class: 'muted', text: c.detail }),
          c.link ? h('a', { href: c.link, text: c.status === 'ok' || c.status === 'off' ? 'Open' : 'Fix or review' }) : null))))),
      h('p', { class: 'muted', text: 'Green is fine, amber needs a look soon, red needs action, grey is switched off on purpose. Updates every minute.' }));
    clearTimeout(timer);
    timer = setTimeout(() => { if (body.isConnected) load(); }, 60000);
  };
  body.append(h('p', { class: 'muted', text: 'Checking...' }));
  load();
}

async function viewAdmin(app) {
  document.title = 'Galleries - ' + site.title;
  const main = await adminPage(app, 'galleries', 'Galleries');
  if (!main) return;
  const [gals, folders, homeOrder] = await Promise.all([api('/api/admin/galleries'), api('/api/admin/folders'), api('/api/admin/settings/home')]);
  main.append(newGalleryPanel((folders.data || []).filter(f => !f.galleryId).map(f => f.path)));
  const list = gals.data || [];
  main.append(h('h2', { text: `Galleries (${list.length})` }));
  if (!list.length) return main.append(h('p', { class: 'muted', text: 'No galleries yet. Create one from a folder above.' }));
  const manual = (homeOrder.data && homeOrder.data.order) === 'manual';
  main.append(h('div', { class: 'gal-list' }, list.map((g, i) => galleryRow(g, manual ? { first: i === 0, last: i === list.length - 1 } : null))));
}

const visShort = { 0: 'Public', 1: 'Unlisted', 2: 'Protected', 3: 'Specific people' };
function galleryRow(g, move) {
  const settingsHref = '/admin/galleries/' + g.id;
  const expired = g.expiresAt && new Date(/Z$/.test(g.expiresAt) ? g.expiresAt : g.expiresAt + 'Z') <= new Date();
  const moveBtns = move ? h('div', { class: 'gal-move' },
    h('button', { text: '\u25B2', title: 'Move up', disabled: move.first, onclick: async () => { await api(`/api/admin/galleries/${g.id}/move`, { method: 'POST', body: { direction: 'up' } }); route(); } }),
    h('button', { text: '\u25BC', title: 'Move down', disabled: move.last, onclick: async () => { await api(`/api/admin/galleries/${g.id}/move`, { method: 'POST', body: { direction: 'down' } }); route(); } })) : null;
  return h('div', { class: 'gal-row' },
    moveBtns,
    h('a', { href: settingsHref, class: 'gal-thumb' }, g.cover ? h('img', { src: `/img/${g.cover}/thumb`, alt: '', loading: 'lazy' }) : h('span', { class: 'muted', text: 'No cover' })),
    h('div', { class: 'gal-info' },
      h('a', { href: settingsHref, class: 'gal-name', text: g.title }),
      h('div', { class: 'chips' },
        h('span', { class: 'chip v' + g.visibility, text: visShort[g.visibility] }),
        h('span', { class: 'chip', text: plural(g.count, 'photo') }),
        g.eventDate ? h('span', { class: 'chip', text: eventLine({ eventDate: g.eventDate }) }) : null,
        g.pending ? h('span', { class: 'chip warn', text: `${g.pending} pending` }) : null,
        expired ? h('span', { class: 'chip warn', text: 'Expired' }) : null,
        g.hasPassword ? h('span', { class: 'chip', text: 'Password' }) : null),
      h('div', { class: 'muted', text: '/g/' + g.slug })),
    h('div', { class: 'gal-actions' },
      h('a', { class: 'btn', href: '/g/' + encodeURIComponent(g.slug), text: 'View' }),
      h('a', { class: 'btn primary', href: settingsHref, text: 'Settings' })));
}

async function adminLogin(app) {
  document.title = 'Admin sign in - ' + site.title;
  const meRes = await api('/api/admin/me');
  if (me) me.admin = false;
  const info = meRes.data || {};
  const sso = h('a', { class: 'btn primary', href: '/auth/login?next=' + encodeURIComponent('/admin'), text: 'Sign in with SSO' });
  const note = info.signedInAs ? h('p', { class: 'muted', text: `You are signed in as ${info.signedInAs}, but this account is not an admin.` }) : null;
  if (info.passwordLogin === false) {
    app.append(h('div', { class: 'center' }, h('h1', { text: 'Admin' }), note, info.sso ? sso : h('p', { class: 'muted', text: 'Admin sign-in is not available.' })));
    return;
  }
  const needUser = !!info.userRequired;
  const user = needUser ? h('input', { type: 'text', placeholder: 'Username', autocomplete: 'username' }) : null;
  const input = h('input', { type: 'password', placeholder: 'Admin password', autocomplete: 'current-password' });
  const err = h('div', { class: 'err' });
  const go = async () => {
    const r = await api('/api/admin/login', { method: 'POST', body: { username: user ? user.value : '', password: input.value } });
    if (r.ok) route(); else err.textContent = r.status === 429 ? 'Too many attempts, wait a minute.' : needUser ? 'Incorrect username or password.' : 'Incorrect password.';
  };
  if (user) user.addEventListener('keydown', e => { if (e.key === 'Enter') input.focus(); });
  input.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
  app.append(h('div', { class: 'center' }, h('h1', { text: 'Admin' }), note, user, input, h('button', { class: 'primary', text: 'Sign in', onclick: go }), err, info.sso ? sso : null));
  (user || input).focus();
}

const visLabels = { 0: 'Public (listed on home page)', 1: 'Unlisted (anyone with the link)', 2: 'Protected (password or share link)', 3: 'Specific people (listed sign-in emails)' };

function checkRow(label, checked) {
  const box = h('input', { type: 'checkbox', checked });
  return { box, el: h('label', {}, box, ' ' + label) };
}

function newGalleryPanel(folders) {
  if (!folders.length) return h('div', { class: 'panel' }, h('h3', { text: 'New gallery from a library folder' }), h('p', { class: 'muted', text: 'Every folder in the photo library already has a gallery. Add a new folder to the library, then reload this page.' }));
  const title = h('input', { type: 'text', placeholder: 'Gallery title' });
  const folder = h('select', {}, folders.map(f => h('option', { value: f, text: f })));
  const vis = h('select', {}, Object.entries(visLabels).map(([v, t]) => h('option', { value: v, text: t, selected: v === '2' })));
  const out = h('span', { class: 'muted' });
  return h('div', { class: 'panel' }, h('h3', { text: 'New gallery from a library folder' }),
    h('div', { class: 'row' }, title, folder, vis,
      h('button', { class: 'primary', text: 'Create & scan', onclick: async () => {
        out.textContent = 'Scanning...';
        const r = await api('/api/admin/galleries', { method: 'POST', body: { title: title.value, folder: folder.value, visibility: Number(vis.value) } });
        if (r.ok) go('/admin/galleries/' + r.data.gallery.id); else out.textContent = typeof r.data === 'string' ? r.data : 'Failed';
      } }), out));
}

async function viewSettings(app, section) {
  document.title = 'Site settings - ' + site.title;
  const titles = { general: 'Site and appearance', watermark: 'Watermark', review: 'Caption suggestions', tags: 'Tags', groups: 'Groups', license: 'License', 'visitor-stats': 'Visitor tracking', email: 'Email alerts', cache: 'Thumbnail cache' };
  if (!titles[section]) section = 'general';
  const main = await adminPage(app, 'settings/' + section, titles[section]);
  if (!main) return;
  if (section === 'watermark') return watermarkSection(main);
  if (section === 'general') { main.append(siteNamePanel(), await themePanel(), homePanel(), await linkPreviewPanel()); return; }
  if (section === 'review') return main.append(await captionReviewPanel());
  if (section === 'tags') return main.append(await tagPanel());
  if (section === 'groups') return main.append(await groupsPanel());
  if (section === 'license') return main.append(await licensePanel());
  if (section === 'visitor-stats') return main.append(statsSettingsPanel());
  if (section === 'email') return main.append(emailPanel());
  main.append(cachePanel());
}

async function watermarkSection(app) {
  const r = await api('/api/admin/settings');
  const w = r.data.watermark;
  let hasPng = r.data.hasPng;
  const modeSel = h('select', {}, [['none', 'None'], ['text', 'Text'], ['png', 'PNG image']].map(([v, t]) => h('option', { value: v, text: t, selected: v === w.mode })));
  const text = h('input', { type: 'text', value: w.text || '', placeholder: '(c) Your Name', maxlength: 80 });
  const pos = h('select', {}, ['top-left', 'top-right', 'bottom-left', 'bottom-right', 'center'].map(v => h('option', { value: v, text: v, selected: v === w.position })));
  const op = h('input', { type: 'range', min: 10, max: 100, value: w.opacity });
  const sz = h('input', { type: 'range', min: 5, max: 80, value: w.sizePercent });
  const opL = h('span', { class: 'muted', text: w.opacity + '%' }), szL = h('span', { class: 'muted', text: w.sizePercent + '% of width' });
  const pngState = h('span', { class: 'muted' });
  const file = h('input', { type: 'file', accept: 'image/png', class: 'hidden' });
  const preview = h('img', { alt: 'Watermark preview', class: 'wm-preview' });
  const msg = h('span', { class: 'muted' });
  const current = () => ({ mode: modeSel.value, text: text.value, position: pos.value, opacity: Number(op.value), sizePercent: Number(sz.value) });
  const syncUi = () => {
    text.disabled = modeSel.value !== 'text';
    pngState.textContent = hasPng ? 'PNG uploaded' : 'No PNG uploaded';
    opL.textContent = op.value + '%'; szL.textContent = sz.value + '% of width';
  };
  let timer;
  const refresh = () => {
    syncUi(); clearTimeout(timer);
    timer = setTimeout(async () => {
      const pr = await fetch('/api/admin/settings/watermark/preview', { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'pg' }, body: JSON.stringify(current()) });
      if (pr.ok) { const url = URL.createObjectURL(await pr.blob()); if (preview.src.startsWith('blob:')) URL.revokeObjectURL(preview.src); preview.src = url; msg.textContent = ''; }
      else msg.textContent = 'Preview needs a gallery with photos.';
    }, 250);
  };
  [modeSel, text, pos, op, sz].forEach(el => el.addEventListener('input', refresh));
  file.addEventListener('change', async () => {
    const fd = new FormData(); fd.append('file', file.files[0]);
    const up = await api('/api/admin/settings/watermark/png', { method: 'POST', form: fd });
    if (up.ok) { hasPng = true; toast('PNG saved'); refresh(); } else toast(typeof up.data === 'string' ? up.data : 'Upload failed');
    file.value = '';
  });
  app.append(h('div', { class: 'panel' }, h('h3', { text: 'Watermark' }),
    h('p', { class: 'muted', text: 'Used by galleries that have "Watermark on screen" or "Watermark downloads" enabled. Choose None to disable it everywhere.' }),
    h('div', { class: 'form' },
      h('label', { text: 'Type' }), modeSel,
      h('label', { text: 'Text' }), text,
      h('label', { text: 'PNG image' }), h('div', { class: 'row' }, h('button', { text: 'Upload PNG', onclick: () => file.click() }), file,
        h('button', { class: 'danger', text: 'Remove', onclick: async () => { await api('/api/admin/settings/watermark/png', { method: 'DELETE' }); hasPng = false; refresh(); } }), pngState),
      h('label', { text: 'Position' }), pos,
      h('label', { text: 'Opacity' }), h('div', { class: 'row' }, op, opL),
      h('label', { text: 'Size' }), h('div', { class: 'row' }, sz, szL)),
    h('div', { class: 'row' }, h('button', { class: 'primary', text: 'Save', onclick: async () => {
      const sv = await api('/api/admin/settings/watermark', { method: 'PUT', body: current() });
      toast(sv.ok ? 'Saved' : 'Save failed');
    } }), msg),
    h('p', { class: 'muted', text: 'Preview (not saved until you click Save):' }), preview));
  refresh();
}

const orderLabels = [
  ['event-desc', 'Event date, newest first'], ['event-asc', 'Event date, oldest first'],
  ['name-asc', 'Name, A to Z'], ['name-desc', 'Name, Z to A'],
  ['created-desc', 'Date created, newest first'], ['created-asc', 'Date created, oldest first'],
  ['manual', 'Manual (use the arrows on the admin list)']];

// Suggestions from signed-in visitors, reviewed like merge requests: approve applies, reject drops, and a conflict needs a deliberate "apply anyway".
async function captionReviewPanel() {
  const r = await api('/api/admin/caption-edits');
  if (!r.ok) return h('div', { class: 'panel' }, h('h3', { text: 'Caption suggestions' }), h('p', { class: 'err', text: 'Could not load suggestions.' }));
  const rows = r.data;
  const items = rows.map(e => {
    const msg = h('span', { class: 'muted' });
    const row = h('div', { class: 'review-row' });
    const decide = async (path, force) => {
      const res = await api(`/api/admin/caption-edits/${e.id}/${path}`, { method: 'POST', body: force ? { force: true } : {} });
      if (res.status === 409) { msg.textContent = 'The text changed after it was suggested. Apply anyway to replace it, or reject.'; return; }
      if (!res.ok) { msg.textContent = 'Failed. Try again.'; return; }
      row.remove();
    };
    row.append(
      h('strong', { text: `${e.photoName} \u00B7 ${e.field === 'alt' ? 'alt text' : 'caption'}` }),
      h('div', { class: 'muted', text: `Suggested by ${e.name || 'a signed-in visitor'} on ${new Date(e.createdAt + (/Z$/.test(e.createdAt) ? '' : 'Z')).toLocaleString()}` }),
      h('div', { class: 'diff-old', text: `Now: ${e.current || '(empty)'}` }),
      h('div', { class: 'diff-new', text: `Suggested: ${e.proposed || '(empty)'}` }),
      e.conflict ? h('div', { class: 'err', text: 'Conflict: the text changed after this was suggested.' }) : null,
      h('div', { class: 'row' },
        h('button', { class: 'primary', text: 'Approve', onclick: () => decide('approve', false) }),
        e.conflict ? h('button', { text: 'Apply anyway', onclick: () => decide('approve', true) }) : null,
        h('button', { text: 'Reject', onclick: () => decide('reject', false) }), msg));
    return row;
  });
  return h('div', { class: 'panel' }, h('h3', { text: `Caption suggestions (${rows.length})` }),
    h('p', { class: 'muted', text: 'Suggestions from signed-in visitors. Approve to apply, reject to drop. A conflict means the text changed after the suggestion was made.' }),
    items.length ? items : h('p', { class: 'muted', text: 'Nothing waiting for review.' }));
}

// Global tags: any label you choose. Visitors see them and can search them. Create, rename, merge and delete them here;
// put them on photos from the viewer's Edit sheet, or on many photos at once with Select in a gallery.
async function tagPanel() {
  const msg = h('span', { class: 'muted' });
  const listBox = h('div');
  let tags = [], view = 'list', sort = 'name', filter = '';
  const used = (t) => t.photos + t.galleries;
  const counts = (t) => [t.photos ? plural(t.photos, 'photo') : null, t.galleries ? (t.galleries === 1 ? '1 gallery' : t.galleries + ' galleries') : null].filter(Boolean).join(' \u00B7 ') || 'not used yet';
  const errText = (res) => typeof res.data === 'string' && res.data ? res.data : 'That did not work. Try again.';

  const load = async () => {
    const r = await api('/api/admin/tags');
    if (!r.ok) { listBox.replaceChildren(h('p', { class: 'err', text: 'Could not load tags.' })); return; }
    tags = r.data;
    tagColorMap = {};
    for (const t of tags) if (t.color) tagColorMap[t.name.toLowerCase()] = t.color;
    draw();
  };

  const newBox = h('input', { type: 'text', maxlength: 40, placeholder: 'New tag, e.g. Sunset', 'aria-label': 'New tag name' });
  const create = async () => {
    const r = await api('/api/admin/tags', { method: 'POST', body: { name: newBox.value } });
    if (!r.ok) { msg.textContent = errText(r); return; }
    msg.textContent = r.data.created ? `Created #${r.data.name}` : `#${r.data.name} already exists`;
    newBox.value = ''; newBox.focus(); load();
  };
  newBox.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); create(); } });

  const search = h('input', { type: 'search', placeholder: 'Filter tags', 'aria-label': 'Filter tags' });
  search.addEventListener('input', () => { filter = search.value.trim().toLowerCase(); draw(); });
  const sortSel = h('select', { 'aria-label': 'Sort tags' }, [['name', 'A to Z'], ['used', 'Most used'], ['unused', 'Unused first']].map(([v, t]) => h('option', { value: v, text: t })));
  sortSel.onchange = () => { sort = sortSel.value; draw(); };
  const viewBtns = h('div', { class: 'tabs tight' });
  const drawViewBtns = () => viewBtns.replaceChildren(...[['list', 'List'], ['cloud', 'Cloud']].map(([v, t]) =>
    h('button', { class: 'tab' + (view === v ? ' on' : ''), text: t, onclick: () => { view = v; draw(); } })));

  const row = (t) => {
    const actions = h('div', { class: 'row tag-actions' });
    const nameEl = tagPill(t.name, 'tag-name');
    const reset = () => { nameEl.hidden = false; showButtons(); };
    const showButtons = () => actions.replaceChildren(
      h('button', { text: 'Rename', onclick: () => {
        const input = h('input', { type: 'text', maxlength: 40, value: t.name, 'aria-label': 'New name' });
        nameEl.hidden = true;
        const save = async () => {
          const r = await api(`/api/admin/tags/${t.id}`, { method: 'PUT', body: { name: input.value } });
          if (!r.ok) { msg.textContent = errText(r); return; }
          msg.textContent = `Renamed to #${r.data.name}`; load();
        };
        input.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); else if (e.key === 'Escape') reset(); });
        actions.replaceChildren(input, h('button', { class: 'primary', text: 'Save', onclick: save }), h('button', { text: 'Cancel', onclick: reset }));
        input.focus(); input.select();
      } }),
      h('button', { text: 'Color', onclick: () => {
        const setColor = async (color) => {
          const r = await api(`/api/admin/tags/${t.id}/color`, { method: 'PUT', body: { color } });
          if (!r.ok) { msg.textContent = errText(r); return; }
          msg.textContent = color ? `#${t.name} is now ${color}` : `#${t.name} picks its own colour again`; load();
        };
        const custom = h('input', { type: 'color', value: tagColor(t.name), 'aria-label': 'Custom colour' });
        custom.addEventListener('change', () => setColor(custom.value));
        actions.replaceChildren(
          ...tagPalette.map(c => { const b = h('button', { type: 'button', class: 'swatch' + (c === tagColor(t.name) ? ' on' : ''), title: c, 'aria-label': 'Use ' + c, onclick: () => setColor(c) }); b.style.background = c; return b; }),
          custom, h('button', { text: 'Auto', title: 'Pick a colour from the name', onclick: () => setColor('') }), h('button', { text: 'Cancel', onclick: reset }));
      } }),
      tags.length > 1 ? h('button', { text: 'Merge into...', onclick: () => {
        const sel = h('select', { 'aria-label': 'Merge into' }, tags.filter(x => x.id !== t.id).map(x => h('option', { value: x.id, text: '#' + x.name })));
        actions.replaceChildren(h('span', { class: 'muted', text: `Move everything tagged #${t.name} onto` }), sel,
          h('button', { class: 'primary', text: 'Merge', onclick: async () => {
            const r = await api(`/api/admin/tags/${t.id}/merge`, { method: 'POST', body: { intoId: Number(sel.value) } });
            if (!r.ok) { msg.textContent = errText(r); return; }
            msg.textContent = `Merged #${t.name} into #${r.data.into}`; load();
          } }), h('button', { text: 'Cancel', onclick: reset }));
      } }) : null,
      h('a', { class: 'btn', href: '/search?tag=' + encodeURIComponent(t.name), title: 'Search for this tag (public galleries only)', text: 'Search' }),
      h('button', { class: 'danger', text: 'Delete', onclick: async () => {
        if (!confirm(used(t) ? `Delete the tag "${t.name}"? It is removed from ${counts(t)}.` : `Delete the tag "${t.name}"?`)) return;
        const r = await api(`/api/admin/tags/${t.id}`, { method: 'DELETE' });
        if (r.ok) { msg.textContent = `Deleted #${t.name}`; load(); }
      } }));
    showButtons();
    return h('div', { class: 'tag-row' }, nameEl, h('span', { class: 'muted tag-count', text: counts(t) }), used(t) ? null : h('span', { class: 'chip warn', text: 'unused' }), h('span', { class: 'grow' }), actions);
  };

  function draw() {
    drawViewBtns();
    let shown = tags.filter(t => !filter || t.name.toLowerCase().includes(filter));
    shown = [...shown].sort(sort === 'used' ? (a, b) => used(b) - used(a) || a.name.localeCompare(b.name)
      : sort === 'unused' ? (a, b) => (used(a) === 0 ? 0 : 1) - (used(b) === 0 ? 0 : 1) || a.name.localeCompare(b.name)
      : (a, b) => a.name.localeCompare(b.name));
    if (!tags.length) { listBox.replaceChildren(h('p', { class: 'muted', text: 'No tags yet. Create one above, or add tags from a photo\'s Edit sheet.' })); return; }
    if (!shown.length) { listBox.replaceChildren(h('p', { class: 'muted', text: 'No tag matches that filter.' })); return; }
    if (view === 'cloud') {
      const max = Math.max(1, ...tags.map(used));
      listBox.replaceChildren(h('div', { class: 'tag-cloud' }, shown.map(t => {
        const b = h('button', { class: 'cloud-tag' + (used(t) ? '' : ' unused'), text: '#' + t.name, title: counts(t),
          onclick: () => { view = 'list'; search.value = t.name; filter = t.name.toLowerCase(); draw(); } });
        b.style.fontSize = (13 + Math.round(17 * used(t) / max)) + 'px';
        b.style.setProperty('--tag-c', tagColor(t.name));
        return b;
      })), h('p', { class: 'muted', text: 'Bigger means used on more photos and galleries. Click a tag to manage it.' }));
    } else listBox.replaceChildren(...shown.map(row));
  }

  load();
  return h('div', { class: 'panel' }, h('h3', { text: 'Tags' }),
    h('p', { class: 'muted', text: 'Tags are labels you choose, about anything. Visitors see them under photos and galleries and can search by them. Put them on one photo from its Edit sheet in the viewer, on a whole gallery from its settings, or on many photos at once: open a gallery, click Select, tick photos and click Tag.' }),
    h('div', { class: 'row' }, newBox, h('button', { class: 'primary', text: 'Create tag', onclick: create }), msg),
    h('div', { class: 'row tag-tools' }, search, sortSel, h('span', { class: 'grow' }), viewBtns),
    listBox);
}

// Groups: named lists of email addresses kept here, not in the identity provider. Invite a group to a Specific people gallery
// from that gallery's settings. A person can be in several groups, and changes apply to access right away.
async function groupsPanel() {
  const body = h('div');
  const panel = h('div', { class: 'panel' }, h('h3', { text: 'Groups' }),
    h('p', { class: 'muted', text: 'A group is a named list of email addresses, such as Family or Team parents. Invite a group to a Specific people gallery from that gallery\'s settings. A person can be in several groups, and changes apply right away.' }), body);
  const errText = (res) => typeof res.data === 'string' && res.data ? res.data : 'That did not work. Try again.';
  const emailsOf = (text) => text.split(/[\s,;]+/).filter(Boolean);

  const render = async () => {
    const [gr, pr] = await Promise.all([api('/api/admin/groups'), api('/api/admin/people')]);
    if (!gr.ok || !pr.ok) { body.replaceChildren(h('p', { class: 'err', text: 'Could not load groups.' })); return; }
    const groups = gr.data, people = pr.data;
    const nameOf = new Map(groups.map(g => [g.id, g.name]));
    const groupsOf = new Map(people.map(p => [p.email, p.groupIds]));

    const createMsg = h('span', { class: 'muted' });
    const newName = h('input', { type: 'text', maxlength: 60, placeholder: 'New group name', 'aria-label': 'New group name' });
    const create = async () => {
      const res = await api('/api/admin/groups', { method: 'POST', body: { name: newName.value, emails: [] } });
      if (res.ok) render(); else createMsg.textContent = errText(res);
    };
    newName.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); create(); } });

    const cards = groups.map(g => {
      const msg = h('span', { class: 'muted' });
      const name = h('input', { type: 'text', maxlength: 60, value: g.name, 'aria-label': 'Group name' });
      const members = h('textarea', { rows: 5, placeholder: 'name@example.com, one per line', 'aria-label': 'Members of ' + g.name });
      let ready = false;   // the member list is fetched when the card is first opened; never save an unloaded (empty) list
      const count = `${g.members} member${g.members === 1 ? '' : 's'}, ${g.galleries} galler${g.galleries === 1 ? 'y' : 'ies'}`;
      const card = h('details', { class: 'group-card' }, h('summary', {}, h('b', { text: g.name }), h('span', { class: 'muted', text: ' ' + count })));
      card.addEventListener('toggle', async () => {
        if (!card.open || ready) return;
        const res = await api(`/api/admin/groups/${g.id}`);
        if (res.ok) { members.value = res.data.emails.join('\n'); ready = true; }
      });
      card.append(h('div', { class: 'form1' },
        field('Name', name),
        field('Members', members, 'One email address per line. A person can also be in other groups.'),
        h('div', { class: 'row' },
          h('button', { class: 'primary', type: 'button', text: 'Save group', onclick: async () => {
            const payload = ready ? { name: name.value, emails: emailsOf(members.value) } : { name: name.value };
            const res = await api(`/api/admin/groups/${g.id}`, { method: 'PUT', body: payload });
            if (res.ok) render(); else msg.textContent = errText(res);
          } }),
          h('button', { class: 'danger', type: 'button', text: 'Delete group...', onclick: async () => {
            if (!confirm(`Delete the group "${g.name}"? It is removed from every gallery it was invited to. The people stay in their other groups.`)) return;
            const res = await api(`/api/admin/groups/${g.id}`, { method: 'DELETE' });
            if (res.ok) render(); else msg.textContent = errText(res);
          } }), msg)));
      return card;
    });

    // one person, any number of groups
    const personMsg = h('span', { class: 'muted' });
    const email = h('input', { type: 'email', maxlength: 200, placeholder: 'person@example.com', 'aria-label': 'Email address' });
    const boxes = groups.map(g => ({ id: g.id, name: g.name, box: h('input', { type: 'checkbox' }) }));
    const setChecks = (ids) => boxes.forEach(b => { b.box.checked = ids.includes(b.id); });
    email.addEventListener('input', () => { const ids = groupsOf.get(email.value.trim().toLowerCase()); if (ids) setChecks(ids); });
    const savePerson = async () => {
      const res = await api('/api/admin/people/groups', { method: 'PUT', body: { email: email.value, groupIds: boxes.filter(b => b.box.checked).map(b => b.id) } });
      if (res.ok) render(); else personMsg.textContent = errText(res);
    };
    const rows = people.map(p => h('div', { class: 'row group-person' },
      h('span', { text: p.email }),
      h('span', { class: 'muted', text: p.groupIds.map(i => nameOf.get(i)).filter(Boolean).join(', ') }),
      h('button', { type: 'button', text: 'Edit', onclick: () => { email.value = p.email; setChecks(p.groupIds); email.focus(); } })));

    body.replaceChildren(h('div', {},
      h('div', { class: 'row' }, newName, h('button', { class: 'primary', type: 'button', text: 'Create group', onclick: create }), createMsg),
      cards.length ? cards : h('p', { class: 'muted', text: 'No groups yet.' }),
      h('h4', { text: 'People' }),
      h('p', { class: 'muted', text: 'Put one person in as many groups as you like: tick every group they belong to. Type an address that is already listed to see and change its groups.' }),
      groups.length
        ? h('div', { class: 'form1' }, field('Email', email),
            field('Groups', h('div', {}, boxes.map(b => h('label', { class: 'group-pick' }, b.box, ' ' + b.name)))),
            h('div', { class: 'row' }, h('button', { class: 'primary', type: 'button', text: 'Save person', onclick: savePerson }), personMsg))
        : h('p', { class: 'muted', text: 'Create a group first.' }),
      rows.length ? rows : h('p', { class: 'muted', text: 'Nobody is in a group yet.' })));
  };
  await render();
  return panel;
}

async function themePanel() {
  const r = await api('/api/admin/settings/theme');
  if (!r.ok) return h('div', { class: 'panel' }, h('h3', { text: 'Appearance' }), h('p', { class: 'err', text: 'Could not load the theme settings.' }));
  const t = r.data;
  const def = h('select', {}, t.themes.map(x => h('option', { value: x.id, text: x.label, selected: x.id === t.default })));
  const allow = h('input', { type: 'checkbox', checked: !!t.allowChoice });
  const msg = h('span', { class: 'muted' });
  return h('div', { class: 'panel' }, h('h3', { text: 'Appearance' }),
    h('p', { class: 'muted', text: 'The theme new visitors see. When visitors may choose, a Theme menu appears in the header and each visitor’s pick is remembered in their own browser. Photos in the full-screen viewer always sit on black.' }),
    h('div', { class: 'row' }, h('label', {}, 'Default theme ', def)),
    h('div', { class: 'row' }, h('label', {}, allow, ' Let visitors choose their own theme')),
    h('div', { class: 'row' }, h('button', { class: 'primary', text: 'Save', onclick: async () => {
      const res = await api('/api/admin/settings/theme', { method: 'PUT', body: { default: def.value, allowVisitorChoice: allow.checked } });
      if (!res.ok) { msg.textContent = 'Could not save'; return; }
      site.theme = res.data; applyTheme(); renderHeader(); msg.textContent = 'Saved';
    } }), msg));
}

async function licensePanel() {
  const r = await api('/api/admin/settings/license');
  if (!r.ok) return h('div', { class: 'panel' }, h('h3', { text: 'License agreement' }), h('p', { class: 'err', text: 'Could not load the license settings.' }));
  const l = r.data.license, def = r.data.defaultText;
  const cb = (v) => h('input', { type: 'checkbox', checked: !!v });
  const enabled = cb(l.enabled), require = cb(l.requireAcceptance);
  const title = h('input', { type: 'text', value: l.title, maxlength: 100 });
  const holder = h('input', { type: 'text', value: l.holder || '', maxlength: 100, placeholder: site.title });
  const contact = h('input', { type: 'text', value: l.contact || '', maxlength: 200, placeholder: 'you@example.com or https://example.com/contact' });
  const text = h('textarea', { rows: 16, maxlength: r.data.maxText, class: 'license-edit' }); text.value = l.text;
  const msg = h('span', { class: 'muted' });
  const refresh = async () => { const v = await api('/api/license'); if (v.ok) { license = v.data; renderFooter(); } };
  const log = await api('/api/admin/license/acceptances');
  const acceptLog = !log.ok ? null : h('details', { class: 'accept-log' }, h('summary', { text: `Acceptance log (${log.data.total})` }),
    log.data.total ? h('table', {}, h('tr', {}, ['When', 'Who', 'Current wording'].map(x => h('th', { text: x }))),
      log.data.recent.map(a => h('tr', {}, h('td', { text: new Date(a.time).toLocaleString() }),
        h('td', { text: a.signedIn ? (a.name || 'Signed-in account') : 'Anonymous browser' }),
        h('td', { text: a.current ? 'Yes' : 'No, accepted an earlier wording' })))) : h('p', { class: 'muted', text: 'Nobody has accepted yet.' }));
  return h('div', { class: 'panel' }, h('h3', { text: 'License agreement' }),
    h('p', { class: 'muted', text: 'Shown at /license and in the footer. When "require acceptance" is on, visitors must tick an agree box before any download. Acceptance is remembered in their browser until you change the wording. The default is a general-purpose example, not legal advice: have it reviewed for your country before relying on it.' }),
    h('div', { class: 'row' }, h('label', {}, enabled, ' Show the license')),
    h('div', { class: 'row' }, h('label', {}, require, ' Require acceptance before downloading')),
    h('div', { class: 'row' }, h('label', {}, 'Title ', title)),
    h('div', { class: 'row' }, h('label', {}, 'Copyright holder ', holder)),
    h('div', { class: 'row' }, h('label', {}, 'Contact for permissions ', contact)),
    h('p', { class: 'muted', text: 'In the text, {holder}, {site}, {year} and {contact} are filled in automatically.' }),
    text,
    h('div', { class: 'row' },
      h('button', { class: 'primary', text: 'Save', onclick: async () => {
        const res = await api('/api/admin/settings/license', { method: 'PUT', body: { enabled: enabled.checked, requireAcceptance: require.checked, title: title.value, holder: holder.value, contact: contact.value, text: text.value } });
        if (!res.ok) { msg.textContent = 'Could not save'; return; }
        text.value = res.data.text; title.value = res.data.title; msg.textContent = 'Saved'; await refresh();
      } }),
      h('button', { text: 'Reset text to default', onclick: () => { if (confirm('Replace the text with the built-in default? Save to apply.')) text.value = def; } }),
      h('a', { class: 'btn', href: '/license', target: '_blank', rel: 'noopener', text: 'Preview' }), msg), acceptLog);
}

async function linkPreviewPanel() {
  const r = await api('/api/admin/settings/link-previews');
  if (!r.ok) return h('div', { class: 'panel' }, h('h3', { text: 'Link previews' }), h('p', { class: 'err', text: 'Could not load this setting.' }));
  const on = h('input', { type: 'checkbox', checked: !!r.data.enabled });
  const msg = h('span', { class: 'muted' });
  return h('div', { class: 'panel' }, h('h3', { text: 'Link previews and checks' }),
    h('p', { class: 'muted', text: 'Once a day the server also asks each saved link whether it still opens, and tells you on the Overview page and in the gallery settings when one does not. For External links on a gallery, the server looks up the page once to get its title and picture, and keeps a small copy. Visitors only load that copy from your site; they never contact the other site until they click the link. Addresses on your own network are never contacted. Turn this off and External links show as plain cards, and nothing is fetched from those sites.' }),
    h('div', { class: 'row' }, h('label', {}, on, ' Fetch titles and pictures for External links, and check that links still open')),
    h('div', { class: 'row' }, h('button', { class: 'primary', text: 'Save', onclick: async () => {
      const res = await api('/api/admin/settings/link-previews', { method: 'PUT', body: { enabled: on.checked } });
      msg.textContent = res.ok ? 'Saved' : 'Could not save';
    } }), msg));
}

function siteNamePanel() {
  const name = h('input', { type: 'text', value: site.title, maxlength: 80 });
  const msg = h('span', { class: 'muted' });
  return h('div', { class: 'panel' }, h('h3', { text: 'Site name' }),
    h('p', { class: 'muted', text: 'Shown in the page header, as the home page heading and as the browser tab title on the home page.' }),
    h('div', { class: 'row' }, name, h('button', { class: 'primary', text: 'Save', onclick: async () => {
      const r = await api('/api/admin/settings/site', { method: 'PUT', body: { name: name.value } });
      if (!r.ok) { msg.textContent = 'Save failed'; return; }
      site.title = r.data.name; name.value = site.title; renderHeader(); document.title = 'Site settings - ' + site.title; msg.textContent = 'Saved';
    } }), msg));
}

function homePanel() {
  const sel = h('select', {}, orderLabels.map(([v, t]) => h('option', { value: v, text: t })));
  api('/api/admin/settings/home').then(r => { if (r.ok) sel.value = r.data.order; });
  return h('div', { class: 'panel' }, h('h3', { text: 'Home page' }),
    h('p', { class: 'muted', text: 'The order of galleries on the public home page and in the admin list. Galleries without an event date come last when sorting by event date.' }),
    h('div', { class: 'row' }, sel, h('button', { class: 'primary', text: 'Save order', onclick: async () => {
      const r = await api('/api/admin/settings/home', { method: 'PUT', body: { order: sel.value } });
      toast(r.ok ? 'Saved' : 'Save failed');
    } })));
}

function statsSettingsPanel() {
  const enabled = h('input', { type: 'checkbox', checked: true });
  const includeAdmin = h('input', { type: 'checkbox' });
  const useCookie = h('input', { type: 'checkbox', checked: true });
  const days = h('input', { type: 'number', min: 0, max: 3650, value: 180 });
  days.style.width = '110px';
  const msg = h('span', { class: 'muted' });
  api('/api/admin/stats/settings').then(r => { if (r.ok) { enabled.checked = r.data.enabled; days.value = r.data.retentionDays; includeAdmin.checked = !!r.data.includeAdmin; useCookie.checked = r.data.useCookie !== false; } });
  return h('div', { class: 'panel' }, h('h3', { text: 'Visitor statistics' }),
    h('p', { class: 'muted', text: 'Records gallery views, photo views, downloads, password attempts and uploads with the visitor IP address, country (when available) and browser. Visits you make while signed in as admin are not recorded unless you switch that on below (useful for testing). IP addresses are personal data, so keep only what you need.' }),
    h('div', { class: 'row' }, h('label', {}, enabled, ' Record visitor activity')),
    h('div', { class: 'row' }, h('label', {}, includeAdmin, ' Also record my own visits while signed in as admin')),
    h('div', { class: 'row' }, h('label', {}, useCookie, ' Tell browsers apart with an anonymous id cookie (separates people who share an IP address)')),
    h('div', { class: 'row' }, h('label', {}, 'Keep records for ', days, ' days (0 = forever)')),
    h('div', { class: 'row' },
      h('button', { class: 'primary', text: 'Save', onclick: async () => {
        const r = await api('/api/admin/stats/settings', { method: 'PUT', body: { enabled: enabled.checked, retentionDays: Number(days.value) || 0, includeAdmin: includeAdmin.checked, useCookie: useCookie.checked } });
        msg.textContent = r.ok ? 'Saved' : 'Save failed';
      } }),
      h('a', { class: 'btn', href: '/admin/stats', text: 'Open statistics' }),
      h('button', { class: 'danger', text: 'Delete all records...', onclick: async () => {
        if (!confirm('Permanently delete ALL recorded visitor activity?')) return;
        const r = await api('/api/admin/stats', { method: 'DELETE' });
        msg.textContent = r.ok ? `Deleted ${r.data.removed} records` : 'Failed';
      } }), msg));
}

function emailPanel() {
  const cb = (checked) => h('input', { type: 'checkbox', checked });
  const loginAlerts = cb(true), enabled = cb(false), uploads = cb(true), linkAlerts = cb(true), expiring = cb(true), weekly = cb(false), subscribers = cb(true);
  const subCount = h('span', { class: 'muted' });
  api('/api/admin/subscribers/count').then(r => { if (r.ok) subCount.textContent = `${r.data.confirmed} confirmed, ${r.data.pending} waiting to confirm`; });
  const host = h('input', { type: 'text', placeholder: '192.168.2.3' });
  const port = h('input', { type: 'number', min: 1, max: 65535, value: 25 });
  port.style.width = '90px';
  const from = h('input', { type: 'text', placeholder: 'gallery@example.com' });
  const to = h('input', { type: 'text', placeholder: 'you@example.com' });
  const url = h('input', { type: 'text', placeholder: location.origin });
  const days = h('input', { type: 'number', min: 1, max: 30, value: 3 });
  days.style.width = '70px';
  const msg = h('span', { class: 'muted' });
  const outbox = h('div', { class: 'muted' });
  const current = () => ({
    enabled: enabled.checked, host: host.value, port: Number(port.value) || 25, from: from.value, to: to.value,
    siteUrl: url.value || location.origin, guestUploads: uploads.checked, linkAlerts: linkAlerts.checked,
    galleryExpiring: expiring.checked, expiryDays: Number(days.value) || 3, weeklySummary: weekly.checked, loginAlerts: loginAlerts.checked,
    subscribers: subscribers.checked
  });
  const loadOutbox = async () => {
    const r = await api('/api/admin/settings/email/outbox');
    if (!r.ok) return;
    outbox.replaceChildren(r.data.length ? h('table', {}, h('tr', {}, ['When', 'Subject', 'Status'].map(x => h('th', { text: x }))),
      r.data.map(m => h('tr', {}, h('td', { text: new Date(m.createdAt + (/Z$/.test(m.createdAt) ? '' : 'Z')).toLocaleString() }), h('td', { text: m.subject }),
        h('td', { text: m.sentAt ? 'Sent' : m.failed ? 'Gave up: ' + m.error : m.attempts ? `Retrying (${m.error})` : 'Waiting to send' })))) : 'No alerts sent yet.');
  };
  api('/api/admin/settings/email').then(r => {
    if (!r.ok) return;
    const d = r.data;
    enabled.checked = d.enabled; host.value = d.host; port.value = d.port; from.value = d.from; to.value = d.to; url.value = d.siteUrl || '';
    uploads.checked = d.guestUploads; linkAlerts.checked = d.linkAlerts; expiring.checked = d.galleryExpiring; days.value = d.expiryDays; weekly.checked = d.weeklySummary; loginAlerts.checked = d.loginAlerts !== false; subscribers.checked = d.subscribers !== false;
  });
  loadOutbox();
  return h('div', { class: 'panel' }, h('h3', { text: 'Email alerts' }),
    h('p', { class: 'muted', text: 'Sends you a short email through your mail relay. Mail is queued and retried if the relay is unavailable.' }),
    h('div', { class: 'row' }, h('label', {}, enabled, ' Send email alerts')),
    h('div', { class: 'row' }, h('label', {}, 'Relay ', host, ' port ', port)),
    h('div', { class: 'row' }, h('label', {}, 'Send from ', from)),
    h('div', { class: 'row' }, h('label', {}, 'Send alerts to ', to)),
    h('div', { class: 'row' }, h('label', {}, 'Site address for links ', url)),
    h('div', { class: 'row' }, h('label', {}, uploads, ' A guest uploads photos that need approval')),
    h('div', { class: 'row' }, h('label', {}, linkAlerts, ' A share link is nearly or fully used up')),
    h('div', { class: 'row' }, h('label', {}, expiring, ' A gallery expires within ', days, ' days')),
    h('div', { class: 'row' }, h('label', {}, loginAlerts, ' Every wrong admin sign-in attempt')),
    h('div', { class: 'row' }, h('label', {}, subscribers, ' Let readers subscribe to emails about new galleries and photos'), subCount),
    h('div', { class: 'row' }, h('label', {}, weekly, ' Weekly visitor summary (Mondays, after 08:00 server time)')),
    h('div', { class: 'row' },
      h('button', { class: 'primary', text: 'Save', onclick: async () => {
        const r = await api('/api/admin/settings/email', { method: 'PUT', body: current() });
        msg.textContent = r.ok ? 'Saved' : (r.data || 'Save failed');
      } }),
      h('button', { text: 'Send test email', onclick: async () => {
        msg.textContent = 'Sending...';
        const r = await api('/api/admin/settings/email/test', { method: 'POST', body: current() });
        msg.textContent = r.ok ? 'Test email sent. Check your inbox.' : (r.data || 'Failed');
        loadOutbox();
      } }), msg),
    h('h4', { text: 'Recent alerts' }), outbox);
}

// photo name linking to the full-size image (or, with viewer=true, to the photo page), plus its gallery
function photoLink(p, viewer = false) {
  const page = p.slug ? `/g/${encodeURIComponent(p.slug)}/p/${p.id}` : null;
  const main = viewer && page
    ? h('a', { href: page, text: p.name, title: 'Open in the gallery viewer' })
    : h('a', { href: `/api/admin/photos/${p.id}/full`, target: '_blank', rel: 'noopener', text: p.name, title: 'Open the full-size photo' });
  const gal = !p.gallery ? null : p.slug
    ? h('a', { href: viewer ? `/g/${encodeURIComponent(p.slug)}` : page, text: p.gallery, title: viewer ? 'Open the gallery' : 'Open in the gallery viewer' })
    : p.gallery;
  return h('span', {}, main, gal ? h('span', { class: 'muted' }, ' (', gal, ')') : null);
}

function cachePanel() {
  const info = h('p', { class: 'muted', text: 'Loading...' });
  const msg = h('span', { class: 'muted' });
  let timer;
  const load = async () => {
    clearTimeout(timer);
    const r = await api('/api/admin/cache');
    if (!r.ok) { info.textContent = 'Could not read the cache.'; return; }
    const c = r.data;
    info.textContent = `${fmtBytes(c.bytes)} in ${c.files} files. Stale: ${c.staleFiles} files (${fmtBytes(c.staleBytes)}). Photos missing thumbnails: ${c.missing}. ` +
      (c.busyGalleries ? `${c.busyGalleries} galleries are building thumbnails now.` : 'No thumbnail jobs running.');
    if (c.busyGalleries) timer = setTimeout(() => { if (info.isConnected) load(); }, 3000);
  };
  load();
  return h('div', { class: 'panel' }, h('h3', { text: 'Thumbnail cache' }),
    h('p', { class: 'muted', text: 'Thumbnails and download sizes are built once and stored. Stale files come from replaced photos, deleted photos and old watermark settings.' }), info,
    h('div', { class: 'row' },
      h('button', { text: 'Clean up stale files', onclick: async () => {
        const r = await api('/api/admin/cache/clean', { method: 'POST' });
        msg.textContent = r.ok ? `Removed ${r.data.files} files, freed ${fmtBytes(r.data.bytes)}` : 'Failed'; load();
      } }),
      h('button', { text: 'Generate missing thumbnails', onclick: async () => {
        const r = await api('/api/admin/cache/generate-all', { method: 'POST' });
        msg.textContent = r.ok ? `Checking ${r.data.galleries} galleries in the background` : 'Failed'; setTimeout(load, 800);
      } }),
      h('button', { text: 'Rebuild all thumbnails', onclick: async () => {
        if (!confirm('Delete and rebuild thumbnails for every gallery? This can take a while on big galleries.')) return;
        const r = await api('/api/admin/cache/rebuild-all', { method: 'POST' });
        msg.textContent = r.ok ? `Rebuilding ${r.data.galleries} galleries in the background` : 'Failed'; setTimeout(load, 800);
      } }), msg));
}

function expiredNote(g, text) {
  const past = g.expiresAt && new Date(/Z$/.test(g.expiresAt) ? g.expiresAt : g.expiresAt + 'Z') <= new Date();
  return past ? 'This gallery has EXPIRED, so visitors cannot see it or find it on the home page. Clear the date and save to reopen it.' : text;
}
function countsLine(g) {
  const bits = [`${g.count} photo${g.count === 1 ? '' : 's'}`];
  if (g.rawOnly) bits.push(`${g.count - g.rawOnly} edited, ${g.rawOnly} RAW-only`);
  if (g.pending) bits.push(`${g.pending} awaiting approval`);
  return bits.join(' \u00B7 ');
}
function section(title, hint, open, ...body) {
  const d = h('details', { class: 'section', open });
  d.append(h('summary', {}, h('span', { class: 'section-title', text: title }), hint ? h('span', { class: 'muted', text: hint }) : null),
    h('div', { class: 'section-body' }, body));
  return d;
}
function field(label, el, hint) {
  return h('div', { class: 'field' }, h('label', { text: label }), h('div', {}, el, hint ? h('div', { class: 'muted', text: hint }) : null));
}
const toLocalInput = (iso) => {
  if (!iso) return '';
  const d = new Date(/Z$/.test(iso) ? iso : iso + 'Z'); const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

async function viewGallerySettings(app, id) {
  app = await adminPage(app, 'galleries');
  if (!app) return;
  const gals = await api('/api/admin/galleries');
  const g = (gals.data || []).find(x => x.id === id);
  if (g) { const gt = await api(`/api/admin/galleries/${id}/tags`); if (gt.ok) g.tags = gt.data; }
  if (g) { const pp = await api(`/api/admin/galleries/${id}/people`); g.people = pp.ok ? pp.data : []; }
  if (g) {
    const [allGroups, invited] = await Promise.all([api('/api/admin/groups'), api(`/api/admin/galleries/${id}/groups`)]);
    g.allGroups = allGroups.ok ? allGroups.data : [];
    g.groupIds = invited.ok ? invited.data : [];
  }
  if (!g) document.title = 'Gallery not found - ' + site.title;
  if (!g) return app.append(h('div', { class: 'center' }, h('h1', { text: 'Gallery not found' }), h('a', { class: 'btn', href: '/admin/galleries', text: 'Back to galleries' })));
  document.title = g.title + ' settings - ' + site.title;

  let dirty = false;
  const status = h('span', { class: 'muted' });
  const saveBtn = h('button', { class: 'primary', text: 'Save changes', disabled: true });
  const touch = () => { dirty = true; saveBtn.disabled = false; status.textContent = 'Unsaved changes'; };
  dirtyGuard = (silent) => !dirty || (silent ? false : confirm('You have unsaved changes. Leave this page and discard them?'));
  const track = (el) => { el.addEventListener('input', touch); el.addEventListener('change', touch); return el; };

  // --- General ---
  const title = track(h('input', { type: 'text', value: g.title }));
  const slug = track(h('input', { type: 'text', value: g.slug }));
  const desc = track(h('textarea', { value: g.description || '' }));
  const eventDate = track(h('input', { type: 'date', value: g.eventDate ? g.eventDate.slice(0, 10) : '' }));
  const location_ = track(h('input', { type: 'text', value: g.location || '', placeholder: 'e.g. Field 3, Springfield' }));
  const sortMode = track(h('select', {}, [[0, 'Capture time'], [1, 'File name'], [2, 'Date added']].map(([v, t]) => h('option', { value: v, text: t, selected: v === g.sortMode }))));
  const sortDesc = track(h('input', { type: 'checkbox', checked: g.sortDesc }));
  const coverImg = h('img', { class: 'cover-prev', alt: '' });
  const setCover = (cid) => { coverImg.style.display = cid ? '' : 'none'; if (cid) coverImg.src = `/img/${cid}/thumb`; };
  setCover(g.cover);
  const pickerBox = h('div');
  const coverBtn = h('button', { text: 'Choose cover...', onclick: async () => {
    if (pickerBox.childNodes.length) { pickerBox.replaceChildren(); return; }
    const r = await api('/api/g/' + encodeURIComponent(g.slug));
    const photos = ((r.data && r.data.photos) || []).filter(p => p.approved && !p.rawOnly);
    pickerBox.append(h('p', { class: 'muted', text: 'Click a photo to make it the gallery cover.' }),
      h('div', { class: 'picker' }, photos.map(p => h('img', { src: `/img/${p.id}/thumb`, alt: p.name, title: p.name, loading: 'lazy', onclick: async () => {
        const cr = await api(`/api/admin/galleries/${g.id}/cover`, { method: 'PUT', body: { photoId: p.id } });
        if (cr.ok) { setCover(p.id); toast('Cover updated'); pickerBox.replaceChildren(); }
      } }))));
  } });
  const scanMsg = h('span', { class: 'muted' });
  const folderSelect = track(h('select', {}, h('option', { value: g.folder, text: g.folder, selected: true })));
  api('/api/admin/folders').then(fr => {
    const free = (fr.data || []).filter(f => !f.galleryId && f.path !== g.folder).map(f => f.path);
    folderSelect.append(...free.map(p => h('option', { value: p, text: p })));
  });
  const linkRows = h('div', { class: 'link-rows' });
  const addLinkRow = (l = { label: '', url: '' }) => {
    const label = track(h('input', { type: 'text', placeholder: 'Title (optional for YouTube)', value: l.label || '', maxlength: 60 }));
    const url = track(h('input', { type: 'text', placeholder: 'https://www.youtube.com/watch?v=...', value: l.url || '' }));
    // the cached YouTube preview image, for saved links only (a new link has none until the gallery is saved)
    const thumb = h('img', { class: 'link-thumb', alt: '', hidden: true });
    thumb.onload = () => { thumb.hidden = false; };
    thumb.onerror = () => { thumb.hidden = true; };
    const showThumb = (bust) => {
      const m = url.value.match(/(?:youtu\.be\/|youtube(?:-nocookie)?\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/|live\/|v\/))([A-Za-z0-9_-]{11})/);
      if (!m) { thumb.hidden = true; return; }
      thumb.src = `/vthumb/${m[1]}` + (bust ? `?t=${Date.now()}` : '');
    };
    url.addEventListener('change', () => showThumb(false));
    const isYt = () => /(?:youtu\.be\/|youtube(?:-nocookie)?\.com\/)/.test(url.value);
    const category = track(h('select', { title: 'Video links show in the Videos grid above the photos. External links show below the gallery.' },
      h('option', { value: 'video', text: 'Video' }), h('option', { value: 'external', text: 'External' })));
    category.value = l.category || (isYt() ? 'video' : 'external');
    const button = track(h('input', { type: 'checkbox', checked: !!l.button }));
    const note = track(h('input', { type: 'text', class: 'link-note', placeholder: 'Note (optional), shown with the link', value: l.note || '', maxlength: 200 }));
    const badge = h('span', { class: 'link-health hidden' });
    const move = (dir) => { const sib = dir < 0 ? row.previousElementSibling : row.nextElementSibling; if (!sib) return; dir < 0 ? row.parentNode.insertBefore(row, sib) : row.parentNode.insertBefore(sib, row); touch(); };
    const row = h('div', { class: 'link-row' }, label, url, category,
      h('label', { class: 'link-opt', title: 'Also show this link as a button under the gallery title' }, button, h('span', { text: 'Button under title' })),
      h('button', { title: 'Move up', 'aria-label': 'Move up', text: '↑', onclick: () => move(-1) }),
      h('button', { title: 'Move down', 'aria-label': 'Move down', text: '↓', onclick: () => move(1) }),
      h('button', { class: 'danger', text: 'Remove', onclick: () => { row.remove(); touch(); } }), badge, thumb, note);
    row.get = () => ({ label: label.value.trim(), url: url.value.trim(), category: category.value, button: button.checked, note: note.value.trim() });
    // what the last check found; only for the address that was saved
    row.setHealth = (items) => {
      const u = url.value.trim();
      const it = (items || []).find(x => x.url === u || x.url === u + '/');
      const show = it && (it.status === 'broken' || it.status === 'ok');
      badge.className = 'link-health ' + (show ? (it.status === 'broken' ? 'bad' : 'good') : 'hidden');
      if (!show) return;
      badge.textContent = it.status === 'broken' ? 'Not opening' : 'Opens';
      badge.title = (it.status === 'broken' ? 'This link did not open when last checked' : 'This link opened when last checked') + (it.lastChecked ? ' (' + new Date(it.lastChecked).toLocaleString() + ')' : '');
    };
    // a YouTube link defaults to Video, anything else to External, until the admin chooses
    url.addEventListener('change', () => { if (!l.category) category.value = isYt() ? 'video' : 'external'; });
    row.showThumb = showThumb;
    showThumb(false);
    linkRows.append(row);
  };
  (g.footageLinks || []).forEach(addLinkRow);
  const videoMsg = h('span', { class: 'muted' });
  const showHealth = (items) => [...linkRows.children].forEach(row => row.setHealth(items));
  api(`/api/admin/galleries/${g.id}/links/health`).then(r => { if (r.ok) showHealth(r.data.items); });
  const footage = h('div', {}, linkRows, h('div', { class: 'row' },
    h('button', { text: '+ Add link', onclick: () => { addLinkRow(); touch(); } }),
    h('button', { text: 'Check links', title: 'Ask every saved link whether it still opens. This also happens automatically about once a day.', onclick: async () => {
      videoMsg.textContent = 'Checking...';
      const r = await api(`/api/admin/galleries/${g.id}/links/check`, { method: 'POST' });
      if (!r.ok) { videoMsg.textContent = 'Could not check'; return; }
      if (!r.data.enabled) { videoMsg.textContent = 'Link checks are switched off in Site and appearance'; return; }
      showHealth(r.data.items);
      const bad = r.data.items.filter(x => x.status === 'broken').length;
      videoMsg.textContent = r.data.items.length === 0 ? 'No links saved in this gallery' : bad ? `${bad} of ${r.data.items.length} links did not open` : 'Every link that could be checked opens';
    } }),
    h('button', { text: 'Refresh thumbnails and previews', title: 'Fetch the current YouTube thumbnails, and the title and picture of External links, again for everything saved in this gallery', onclick: async () => {
      videoMsg.textContent = 'Refreshing...';
      const r = await api(`/api/admin/galleries/${g.id}/video-thumbnails/refresh`, { method: 'POST' });
      if (r.ok) [...linkRows.children].forEach(row => row.showThumb(true));
      videoMsg.textContent = !r.ok ? 'Could not refresh' : r.data.videos + r.data.links === 0 ? 'No videos or External links saved in this gallery' : `Refreshed ${r.data.refreshed} of ${r.data.videos} video thumbnails and ${r.data.linksRefreshed} of ${r.data.links} link previews. Reload the gallery page to see them there.`;
    } }), videoMsg));
  const collectLinks = () => [...linkRows.children].map(r => r.get()).filter(l => l.url);
  const tagsInput = tagEditor(g.tags || [], touch);
  const general = section('General', 'Name, date, order, cover', true,
    h('div', { class: 'form1' },
      field('Title', title), field('URL name', slug, `Gallery address: /g/${g.slug}`), field('Description', desc),
      field('Event date', eventDate), field('Location', location_), field('Tags', tagsInput, 'Click + to add a label that helps visitors find this gallery in search. Start typing to pick one already in use.'),
      field('Videos and links', footage, 'Paste YouTube links to show the recordings together on the gallery page (leave the title empty to use the video\'s own title). Preview images are re-checked on YouTube every 12 hours; use Refresh thumbnails and previews to update them now. Choose a category for each link: Video links show in a grid above the photos, External links show below the gallery. Tick Button under title to also show a link as a button beside the title. Only http and https links are kept, up to 30.'),
      field('Photo order', h('div', { class: 'row' }, sortMode, h('label', {}, sortDesc, ' Reverse (newest / Z-A first)'))),
      field('Cover image', h('div', {}, h('div', { class: 'row' }, coverImg, coverBtn), pickerBox)),
      field('Library folder', folderSelect, 'Which library folder this gallery shows. Changing it rescans the gallery and resets the cover.'),
      field('Rescan', h('div', { class: 'row' }, h('button', { text: 'Rescan folder now', onclick: async () => {
        scanMsg.textContent = 'Scanning...';
        const r = await api(`/api/admin/galleries/${g.id}/scan`, { method: 'POST' });
        scanMsg.textContent = r.ok ? `Added ${r.data.added}, removed ${r.data.removed}; ${r.data.paired} paired with RAW, ${r.data.rawOnly} RAW-only` : (typeof r.data === 'string' ? r.data : 'Failed');
      } }), scanMsg), 'Folders are also rescanned automatically.'),
      field('Thumbnails', thumbControl(g), 'Built automatically in the background after each scan. Rebuild after fixing or replacing photos.'),
      field('Delete', h('button', { class: 'danger', text: 'Delete gallery...', onclick: async () => {
        if (!confirm('Remove this gallery from the site? Your original photo files are NOT deleted.')) return;
        dirty = false; await api('/api/admin/galleries/' + g.id, { method: 'DELETE' }); go('/admin/galleries');
      } }))));

  // --- Access & sharing ---
  const vis = track(h('select', {}, Object.entries(visLabels).map(([v, t]) => h('option', { value: v, text: t, selected: Number(v) === g.visibility }))));
  const pw = track(h('input', { type: 'password', placeholder: g.hasPassword ? 'Password set (type to change, - to remove)' : 'No password', autocomplete: 'new-password' }));
  const expires = track(h('input', { type: 'datetime-local', value: toLocalInput(g.expiresAt) }));
  const peopleBox = track(h('textarea', { rows: 4, placeholder: 'name@example.com, one per line', 'aria-label': 'Invited email addresses' }));
  peopleBox.value = (g.people || []).join('\n');
  const groupBoxes = (g.allGroups || []).map(gr => ({ id: gr.id, name: gr.name, box: track(h('input', { type: 'checkbox', checked: (g.groupIds || []).includes(gr.id) })) }));
  const groupsPick = groupBoxes.length
    ? h('div', {}, groupBoxes.map(b => h('label', { class: 'group-pick' }, b.box, ' ' + b.name)))
    : h('span', { class: 'muted', text: 'No groups yet. Create them in the Groups section of the admin settings.' });
  const access = section('Access & sharing', 'Who can open it, passwords, links', true,
    h('div', { class: 'form1' },
      field('Visibility', vis, 'Public shows on the home page. Unlisted needs the link. Protected needs a password or a share link. Specific people opens only for the invited addresses below.'),
      field('Invited emails', peopleBox, 'Used with Specific people. Add addresses before those people have signed in. They sign in with this address, and their provider must have verified it. Needs single sign-on to be set up.'),
      field('Invited groups', groupsPick, 'Everyone in a ticked group can open this gallery when Specific people is selected. Manage groups in the Groups section of the admin settings.'),
      field('Password', pw),
      field('Auto-expire', h('div', { class: 'row' }, expires, h('button', { text: 'Clear', title: 'Keep the gallery open indefinitely', onclick: () => { expires.value = ''; touch(); } })), expiredNote(g, 'After this time the gallery closes for everyone but you. Nothing is deleted. Clear it to keep the gallery open.'))),
    h('h3', { text: 'Share links' }), linksManager(g));

  // --- Downloads ---
  const allowDl = track(h('input', { type: 'checkbox', checked: g.allowDownload }));
  const allowZip = track(h('input', { type: 'checkbox', checked: g.allowZip }));
  const selSizes = (g.allowedSizes || '').split(',').filter(Boolean).map(Number);
  const sizeBoxes = site.sizes.map(px => ({ px, box: track(h('input', { type: 'checkbox', checked: selSizes.length === 0 || selSizes.includes(px) })) }));
  const selFmts = (g.allowedFormats || '').split(',').filter(Boolean);
  const fmtBoxes = site.formats.map(f => ({ f, box: track(h('input', { type: 'checkbox', checked: selFmts.length === 0 || selFmts.includes(f) })) }));
  const downloads = section('Downloads', 'What viewers can take', true,
    h('div', { class: 'form1' },
      field('Downloads', h('label', {}, allowDl, ' Allow viewers to download photos')),
      field('ZIP', h('label', {}, allowZip, ' Allow downloading the whole gallery as a ZIP')),
      field('Sizes offered', h('div', { class: 'row' }, sizeBoxes.map(x => h('label', {}, x.box, ` ${x.px}px`))), 'Long edge. The original file is always available unless watermarking downloads.'),
      field('Formats offered', h('div', { class: 'row' }, fmtBoxes.map(x => h('label', {}, x.box, ' ' + (fmtLabels[x.f] || x.f)))), 'All sized downloads are lossless.')));

  // --- Watermark & uploads ---
  const wmView = track(h('input', { type: 'checkbox', checked: g.watermarkView }));
  const wmDl = track(h('input', { type: 'checkbox', checked: g.watermarkDownload }));
  const guestUp = track(h('input', { type: 'checkbox', checked: g.allowGuestUpload }));
  const wmUp = section('Watermark & uploads', 'Branding and guest photos', true,
    h('div', { class: 'form1' },
      field('On screen', h('label', {}, wmView, ' Watermark photos shown in the gallery'), 'Style is set in Site settings.'),
      field('Downloads', h('label', {}, wmDl, ' Watermark sized downloads'), 'This also blocks original and RAW downloads, which cannot carry a watermark.'),
      field('Guest uploads', h('label', {}, guestUp, ' Let visitors upload photos'), 'Uploads stay hidden until you approve them.')),
    h('p', {}, h('a', { href: '/admin/settings/watermark', text: 'Open site settings (watermark style)' })), pendingManager(g));

  const favSection = section('Favorites', 'Photos visitors hearted', false, favoritesManager(g));

  // --- RAW files (collapsed) ---
  const rawMode = track(h('select', {}, [[0, 'Same folder, same file name'], [1, 'Same file name, any folder'], [2, 'Name starts with the RAW name (IMG_1234-Edit.png)']].map(([v, t]) => h('option', { value: v, text: t, selected: v === g.rawMatchMode }))));
  const allowRaw = track(h('input', { type: 'checkbox', checked: g.allowRaw }));
  const hideRaw = track(h('input', { type: 'checkbox', checked: g.hideRawOnly }));
  const raw = section('RAW files', 'Matching, filter and downloads', false,
    h('div', { class: 'form1' },
      field('Match RAW to image by', rawMode, 'Used on rescan. Rescan the folder after changing this.'),
      field('RAW downloads', h('label', {}, allowRaw, ' Let viewers download the RAW file'), 'Blocked while downloads are watermarked.'),
      field('Curated filter', h('label', {}, hideRaw, ' Hide RAW frames with no edited image until the viewer turns the filter off'))),
    rawLinker(g));

  saveBtn.onclick = async () => {
    if (folderSelect.value !== g.folder && !confirm(`Point this gallery at "${folderSelect.value}"? It will rescan, the cover resets, and hand-made RAW links are lost. Your files are not touched.`)) return;
    const body = {
      folder: folderSelect.value, title: title.value, slug: slug.value, description: desc.value, location: location_.value,
      eventDate: eventDate.value || '', sortMode: Number(sortMode.value), sortDesc: sortDesc.checked,
      visibility: Number(vis.value), expiresAt: expires.value ? new Date(expires.value).toISOString() : '',
      allowDownload: allowDl.checked, allowZip: allowZip.checked,
      allowedSizes: sizeBoxes.every(x => x.box.checked) ? '' : sizeBoxes.filter(x => x.box.checked).map(x => x.px).join(','),
      allowedFormats: fmtBoxes.every(x => x.box.checked) ? '' : fmtBoxes.filter(x => x.box.checked).map(x => x.f).join(','),
      watermarkView: wmView.checked, watermarkDownload: wmDl.checked, allowGuestUpload: guestUp.checked,
      rawMatchMode: Number(rawMode.value), allowRaw: allowRaw.checked, hideRawOnly: hideRaw.checked, footageLinks: collectLinks()
    };
    if (pw.value) body.password = pw.value === '-' ? '' : pw.value;
    const r = await api('/api/admin/galleries/' + g.id, { method: 'PUT', body });
    if (r.ok) await api(`/api/admin/galleries/${g.id}/tags`, { method: 'PUT', body: { names: tagsInput.names() } });
    if (r.ok) {
      const pr = await api(`/api/admin/galleries/${g.id}/people`, { method: 'PUT', body: { emails: peopleBox.value.split(/[\n,]+/) } });
      if (!pr.ok) toast(typeof pr.data === 'string' ? pr.data : 'Could not save the invited emails');
      if (groupBoxes.length) await api(`/api/admin/galleries/${g.id}/groups`, { method: 'PUT', body: { ids: groupBoxes.filter(b => b.box.checked).map(b => b.id) } });
    }
    if (r.ok) { dirty = false; toast('Saved'); route(); }
    else status.textContent = typeof r.data === 'string' ? r.data : 'Save failed';
  };

  app.append(
    h('div', { class: 'toolbar' }, h('a', { class: 'btn', href: '/admin/galleries', text: '\u2190 All galleries' }), h('h1', { text: g.title }), h('span', { class: 'grow' }),
      h('a', { class: 'btn', href: '/admin/stats?gallery=' + g.id, text: 'Statistics' }),
      h('a', { class: 'btn', href: '/g/' + encodeURIComponent(g.slug), text: 'View gallery' })),
    h('p', { class: 'muted counts', text: countsLine(g) }),
    general, access, downloads, wmUp, favSection, raw,
    h('div', { class: 'savebar' }, saveBtn, status));
}

function thumbControl(g) {
  const label = h('span', { class: 'muted' });
  const genBtn = h('button', { text: 'Generate missing', title: 'Build only the thumbnails that do not exist yet', onclick: async () => {
    await api(`/api/admin/galleries/${g.id}/thumbnails/generate`, { method: 'POST' });
    poll();
  } });
  const btn = h('button', { text: 'Rebuild all', onclick: async () => {
    if (!confirm('Delete and rebuild all thumbnails for this gallery? Photos stay slow to open until they are rebuilt.')) return;
    await api(`/api/admin/galleries/${g.id}/thumbnails/rebuild`, { method: 'POST' });
    poll();
  } });
  let timer, started = false;
  const poll = async () => {
    clearTimeout(timer);
    if (started && !label.isConnected) return;
    started = true;
    const r = await api(`/api/admin/galleries/${g.id}/thumbnails`);
    const t = r.data || {};
    if (t.running) label.textContent = `Building ${t.done} / ${t.total}...`;
    else if (t.queued) label.textContent = 'Queued...';
    else label.textContent = t.missing ? `${t.missing} photos are missing thumbnails` : 'All thumbnails built';
    genBtn.disabled = !!(t.running || t.queued) || !t.missing;
    if (t.running || t.queued) timer = setTimeout(poll, 2000);
  };
  setTimeout(poll, 200);
  return h('div', { class: 'row' }, genBtn, btn, label);
}

function favoritesManager(g) {
  const box = h('div', {}, h('p', { class: 'muted', text: 'Loading...' }));
  const copy = async (text, what) => { try { await navigator.clipboard.writeText(text); toast(`Copied ${what}`); } catch { prompt('Copy', text); } };
  (async () => {
    const r = await api(`/api/admin/galleries/${g.id}/favorites`);
    if (!r.ok) { box.replaceChildren(h('p', { class: 'err', text: 'Could not load favorites.' })); return; }
    const { lists, top } = r.data;
    if (!lists.length) { box.replaceChildren(h('p', { class: 'muted', text: 'Nobody has hearted a photo in this gallery yet.' })); return; }
    const all = new Map();
    lists.forEach(l => l.photos.forEach(p => all.set(p.id, p)));
    const allPhotos = [...all.values()];
    const zipHref = (ids) => `/api/g/${encodeURIComponent(g.slug)}/zip?size=original&raw=true&ids=${ids.join(',')}`;
    const actions = (photos) => h('div', { class: 'row' },
      h('button', { text: 'Copy file names', onclick: () => copy(photos.map(p => p.name).join('\n'), `${photos.length} file names`) }),
      h('button', { text: 'Copy RAW names', onclick: () => copy(photos.filter(p => p.raw).map(p => p.raw).join('\n'), 'RAW file names') }),
      h('a', { class: 'btn', href: zipHref(photos.slice(0, 500).map(p => p.id)), text: 'Download ZIP (originals + RAW)' }));
    box.replaceChildren(
      h('p', { class: 'muted', text: `${lists.length} visitor${lists.length === 1 ? '' : 's'} chose ${allPhotos.length} different photos.` }),
      h('div', { class: 'panel inner' }, h('b', { text: 'All favorites combined' }), actions(allPhotos)),
      top.length ? h('details', {}, h('summary', { text: 'Most favorited photos' }), h('table', {}, h('tr', {}, ['Photo', 'Hearts'].map(x => h('th', { text: x }))), top.map(t => h('tr', {}, h('td', { text: t.name }), h('td', { text: t.count }))))) : null,
      lists.map(l => {
        const grid = h('div', { class: 'photo-cards hidden' }, l.photos.map(p => h('div', { class: 'photo-card' }, h('img', { src: `/img/${p.id}/thumb`, alt: p.name, loading: 'lazy' }), h('div', { class: 'pc-name', text: p.name, title: p.name }))));
        const who = l.name || 'Anonymous visitor';
        return h('div', { class: 'panel inner' },
          h('div', { class: 'row' }, h('b', { text: who }), h('span', { class: 'muted', text: `${l.count} photos \u00B7 updated ${new Date(l.updated).toLocaleString()}${l.ip ? ' \u00B7 ' + l.ip : ''}${l.country ? ' (' + l.country + ')' : ''}` })),
          actions(l.photos), h('button', { text: 'Show photos', onclick: (e) => { grid.classList.toggle('hidden'); e.target.textContent = grid.classList.contains('hidden') ? 'Show photos' : 'Hide photos'; } }), grid);
      }));
  })();
  return box;
}

function linksManager(g) {
  const box = h('div');
  const load = async () => {
    const r = await api(`/api/admin/galleries/${g.id}/links`);
    box.replaceChildren(h('table', {},
      h('tr', {}, ['Label', 'Link', 'Expires', 'Downloads', ''].map(t => h('th', { text: t }))),
      (r.data || []).map(l => {
        const url = `${location.origin}/s/${l.token}`;
        const state = l.revoked ? 'revoked' : (l.expiresAt && new Date(l.expiresAt.endsWith('Z') ? l.expiresAt : l.expiresAt + 'Z') < new Date() ? 'expired' : '');
        return h('tr', {},
          h('td', { text: l.label || '-' }),
          h('td', {}, h('a', { href: url, text: '/s/' + l.token.slice(0, 8) + '...', target: '_blank' }), ' ',
            h('button', { text: 'Copy', onclick: async () => { try { await navigator.clipboard.writeText(url); toast('Copied'); } catch { prompt('Copy link', url); } } })),
          h('td', { text: (l.expiresAt ? fmtDate(l.expiresAt) : 'never') + (state ? ` (${state})` : '') }),
          h('td', { text: `${l.downloadCount}${l.maxDownloads ? ' / ' + l.maxDownloads : ''}` }),
          h('td', {}, l.revoked ? '' : h('button', { class: 'danger', text: 'Revoke', onclick: async () => { await api('/api/admin/links/' + l.id, { method: 'DELETE' }); load(); } })));
      })));
  };
  const label = h('input', { type: 'text', placeholder: 'Label (e.g. Team parents)' });
  const days = h('input', { type: 'number', placeholder: 'Expires in days', min: 1 });
  const max = h('input', { type: 'number', placeholder: 'Max downloads', min: 1 });
  days.style.width = '150px'; max.style.width = '150px';
  load();
  return h('div', {}, h('div', { class: 'row' }, label, days, max,
    h('button', { text: 'Create link', onclick: async () => {
      const r = await api(`/api/admin/galleries/${g.id}/links`, { method: 'POST', body: { label: label.value, expiresInDays: Number(days.value) || null, maxDownloads: Number(max.value) || null } });
      if (r.ok) { label.value = ''; days.value = ''; max.value = ''; load(); }
    } })), box);
}

function pendingManager(g) {
  const box = h('div');
  (async () => {
    const r = await api('/api/g/' + encodeURIComponent(g.slug));
    const pend = ((r.data && r.data.photos) || []).filter(p => !p.approved);
    if (!pend.length) return;
    box.append(h('h3', { text: `Pending guest uploads (${pend.length})` }),
      h('div', { class: 'grid' }, pend.map(p => {
        const it = h('div', { class: 'ph pending' }, h('img', { src: `/img/${p.id}/thumb`, alt: p.name }));
        it.style.width = '180px'; it.style.height = '140px'; it.style.cursor = 'default';
        return h('div', {}, it, h('div', { class: 'row' },
          h('button', { class: 'primary', text: 'Approve', onclick: async () => { await api(`/api/admin/photos/${p.id}/approve`, { method: 'POST' }); route(); } }),
          h('button', { class: 'danger', text: 'Reject', onclick: async () => { await api(`/api/admin/photos/${p.id}`, { method: 'DELETE' }); route(); } })));
      })));
  })();
  return box;
}

function rawLinker(g) {
  const box = h('div');
  const load = async () => {
    box.replaceChildren();
    const r = await api('/api/g/' + encodeURIComponent(g.slug));
    const photos = (r.data && r.data.photos) || [];
    const rawOnly = photos.filter(p => p.rawOnly);
    const candidates = photos.filter(p => !p.raw && !p.rawOnly);
    box.append(h('p', { class: 'muted', text: `${photos.filter(p => p.raw && !p.rawOnly).length} photos have a RAW attached. ${rawOnly.length} RAW files have no edited image.` }));
    if (!rawOnly.length) return;
    const listId = 'dl-' + g.id;
    box.append(h('h3', { text: 'Link a RAW to an edited image by name' }), h('datalist', { id: listId }, candidates.map(p => h('option', { value: p.name }))),
      h('table', {}, rawOnly.map(p => {
        const input = h('input', { type: 'text', placeholder: 'Edited image file name', list: listId });
        return h('tr', {}, h('td', {}, h('img', { src: `/img/${p.id}/thumb`, alt: '', class: 'tiny' })), h('td', { text: p.name }),
          h('td', {}, input, ' ', h('button', { text: 'Link', onclick: async () => {
            const target = candidates.find(c => c.name.toLowerCase() === input.value.trim().toLowerCase());
            if (!target) { toast('No matching image name'); return; }
            const lr = await api(`/api/admin/photos/${target.id}/link-raw`, { method: 'POST', body: { rawPhotoId: p.id } });
            if (lr.ok) { toast('Linked'); load(); } else toast(typeof lr.data === 'string' ? lr.data : 'Failed');
          } })));
      })));
  };
  load();
  return box;
}

// ---------- statistics ----------
const kindLabels = { view_gallery: 'Viewed gallery', view_photo: 'Viewed photo', download: 'Downloaded', zip: 'Downloaded ZIP', favorite: 'Favorited a photo', unlock_ok: 'Unlocked (password)', unlock_fail: 'Wrong password', share_open: 'Opened share link', share_invalid: 'Bad/expired link', upload: 'Uploaded' };

async function viewStats(app) {
  document.title = 'Statistics - ' + site.title;
  app = await adminPage(app, 'stats');
  if (!app) return;
  const qs = new URLSearchParams(location.search);
  const state = { days: Number(qs.get('days') ?? 30), gallery: qs.get('gallery') || '', who: qs.get('who') || 'visitors', kind: '', skip: 0, view: qs.get('view') || 'overview', ip: qs.get('visitor') || (qs.get('ip') ? 'ip:' + qs.get('ip') : ''), group: qs.get('group') || 'cookie', sort: qs.get('sort') || 'recent' };
  const galleries = (await api('/api/admin/galleries')).data || [];

  const body = h('div');
  const rangeSel = h('select', {}, [[7, 'Last 7 days'], [30, 'Last 30 days'], [90, 'Last 90 days'], [365, 'Last year'], [0, 'All time']].map(([v, t]) => h('option', { value: v, text: t, selected: v === state.days })));
  const galSel = h('select', {}, [h('option', { value: '', text: 'All galleries' }), ...galleries.map(g => h('option', { value: g.id, text: g.title, selected: String(g.id) === state.gallery }))]);
  const tabs = h('div', { class: 'tabs' });
  const sync = () => { const q = new URLSearchParams(); if (state.days !== 30) q.set('days', state.days); if (state.gallery) q.set('gallery', state.gallery); if (state.who !== 'visitors') q.set('who', state.who); if (state.ip) q.set('visitor', state.ip); else if (state.view === 'visitors') q.set('view', 'visitors'); if (state.group !== 'cookie') q.set('group', state.group); if (state.view === 'visitors' && state.sort !== 'recent') q.set('sort', state.sort); history.replaceState(history.state, '', '/admin/stats' + (q.toString() ? '?' + q : '')); };
  const q = () => { const p = new URLSearchParams({ days: state.days, who: state.who }); if (state.gallery) p.set('gallery', state.gallery); return p; };

  const load = async () => {
    sync();
    syncViewTabs();
    if (state.ip) return loadVisitor();
    if (state.view === 'visitors') return loadVisitors();
    return loadOverview();
  };

  const whoTabs = (d) => tabs.replaceChildren(...[['visitors', `Visitors (${d.visitorEvents})`], ['bots', `Bots & crawlers (${d.botEvents})`], ['all', 'All']].map(([v, t]) =>
    h('button', { class: 'tab' + (state.who === v ? ' on' : ''), text: t, onclick: () => { state.who = v; state.skip = 0; load(); } })));

  const viewTabs = h('div', { class: 'tabs' });
  const syncViewTabs = () => viewTabs.replaceChildren(...[['overview', 'Overview'], ['visitors', 'By visitor (IP)']].map(([v, t]) =>
    h('button', { class: 'tab' + (!state.ip && state.view === v ? ' on' : ''), text: t, onclick: () => { state.view = v; state.ip = ''; load(); } })));

  const loadOverview = async () => {
    { const gn = (galleries.find(x => String(x.id) === state.gallery) || {}).title; document.title = (gn ? 'Statistics: ' + gn : 'Statistics') + ' - ' + site.title; }
    const r = await api('/api/admin/stats?' + q());
    if (!r.ok) { body.replaceChildren(h('p', { class: 'err', text: 'Could not load statistics.' })); return; }
    const d = r.data;
    whoTabs(d);
    const t = d.totals;
    const tile = (n, label) => h('div', { class: 'tile' }, h('div', { class: 'tile-n', text: String(n) }), h('div', { class: 'muted', text: label }));
    const max = Math.max(1, ...d.byDay.map(x => x.views + x.downloads));
    const chart = d.byDay.length ? h('div', { class: 'bars' }, d.byDay.map(x => {
      const col = h('div', { class: 'bar-col', title: `${x.date}: ${x.views} views, ${x.downloads} downloads` });
      const v = h('div', { class: 'bar views' }), dl = h('div', { class: 'bar dls' });
      v.style.height = (x.views / max * 100) + '%'; dl.style.height = (x.downloads / max * 100) + '%';
      col.append(dl, v); return col;
    })) : h('p', { class: 'muted', text: 'No activity in this period.' });
    body.replaceChildren(
      h('div', { class: 'tiles' }, tile(t.uniqueVisitors, 'Unique IP addresses'), tile(t.galleryViews, 'Gallery views'), tile(t.photoViews, 'Photo views'),
        tile(t.downloads, 'Photo downloads'), tile(t.zips, 'ZIP downloads'), tile(t.shareOpens, 'Share link opens'), tile(t.uploads, 'Guest uploads'), tile(t.unlockFails, 'Wrong passwords')),
      h('div', { class: 'panel' }, h('h3', { text: 'Activity per day' }), chart, h('div', { class: 'muted', text: 'Blue = views, orange = downloads' })),
      h('div', { class: 'panel' }, h('h3', { text: 'Galleries' }), d.topGalleries.length ? h('table', {},
        h('tr', {}, ['Gallery', 'Views', 'Photo views', 'Downloads', 'IPs'].map(x => h('th', { text: x }))),
        d.topGalleries.map(g => h('tr', {}, h('td', {}, h('a', { href: '/admin/stats?gallery=' + g.id, text: g.title })), h('td', { text: g.views }), h('td', { text: g.photoViews }), h('td', { text: g.downloads }), h('td', { text: g.visitors })))) : h('p', { class: 'muted', text: 'Nothing yet.' })),
      h('div', { class: 'two' },
        h('div', { class: 'panel' }, h('h3', { text: 'Top viewed photos' }), d.topPhotos.length ? h('table', {},
          h('tr', {}, ['', 'Photo', 'Views', 'Downloads'].map(x => h('th', { text: x }))),
          d.topPhotos.map(p => h('tr', {}, h('td', {}, h('img', { src: `/img/${p.id}/thumb`, alt: '', class: 'tiny' })), h('td', {}, photoLink(p, true)), h('td', { text: p.views }), h('td', { text: p.downloads })))) : h('p', { class: 'muted', text: 'Nothing yet.' })),
        h('div', { class: 'panel' }, h('h3', { text: 'Top downloads' }), d.topDownloads.length ? h('table', {},
          h('tr', {}, ['', 'Photo', 'Downloads', 'IPs'].map(x => h('th', { text: x }))),
          d.topDownloads.map(p => h('tr', {}, h('td', {}, h('img', { src: `/img/${p.id}/thumb`, alt: '', class: 'tiny' })), h('td', {}, photoLink(p, true)), h('td', { text: p.downloads }), h('td', { text: p.visitors })))) : h('p', { class: 'muted', text: 'No downloads yet.' }))),
      h('div', { class: 'two' },
        h('div', { class: 'panel' }, h('h3', { text: 'Countries' }), d.countries.length ? h('table', {}, h('tr', {}, ['Country', 'IPs', 'Events'].map(x => h('th', { text: x }))),
          d.countries.map(c => h('tr', {}, h('td', { text: c.country }), h('td', { text: c.visitors }), h('td', { text: c.events })))) : h('p', { class: 'muted', text: 'Country is only known while Cloudflare proxies the site.' })),
        h('div', { class: 'panel' }, h('h3', { text: 'What was downloaded' }), d.downloadsBy.length ? h('table', {}, h('tr', {}, ['Type', 'Count'].map(x => h('th', { text: x }))),
          d.downloadsBy.map(x => h('tr', {}, h('td', { text: x.detail || '-' }), h('td', { text: x.count })))) : h('p', { class: 'muted', text: 'No downloads yet.' }))),
      activityPanel(),
      backupPanel());
  };

  const uaLabel = (ua) => {
    if (!ua) return 'Unknown browser';
    const browser = /Edg\//.test(ua) ? 'Edge' : /OPR\/|Opera/.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox' : /CriOS|Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : /curl|wget|python|bot|crawl|spider/i.test(ua) ? 'Bot' : 'Browser';
    const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac OS X|Macintosh/.test(ua) ? 'Mac' : /CrOS/.test(ua) ? 'Chromebook' : /Linux/.test(ua) ? 'Linux' : '';
    return os ? `${browser} on ${os}` : browser;
  };
  const ago = (iso) => { const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000); return m < 1 ? 'just now' : m < 60 ? `${m} min ago` : m < 2880 ? `${Math.round(m / 60)} h ago` : `${Math.round(m / 1440)} days ago`; };
  const visitorHref = (key) => '/admin/stats?visitor=' + encodeURIComponent(key) + '&group=' + state.group + '&days=' + state.days + (state.gallery ? '&gallery=' + state.gallery : '');

  const loadVisitors = async () => {
    document.title = 'Statistics by visitor - ' + site.title;
    const p = q(); p.set('sort', state.sort); p.set('group', state.group); p.set('take', 100);
    const r = await api('/api/admin/stats/visitors?' + p);
    if (!r.ok) { body.replaceChildren(h('p', { class: 'err', text: 'Could not load visitors.' })); return; }
    whoTabs(r.data);
    const sortSel = h('select', {}, [['recent', 'Most recent'], ['events', 'Most active'], ['photos', 'Most photo views'], ['downloads', 'Most downloads']].map(([v, t]) => h('option', { value: v, text: t, selected: v === state.sort })));
    sortSel.onchange = () => { state.sort = sortSel.value; load(); };
    const groupSel = h('select', {}, [['cookie', 'Visitor (cookie, else IP + browser)'], ['browser', 'IP + browser'], ['ip', 'IP address only']].map(([v, t]) => h('option', { value: v, text: t, selected: v === state.group })));
    groupSel.onchange = () => { state.group = groupSel.value; load(); };
    const rows = r.data.items;
    body.replaceChildren(h('div', { class: 'panel' },
      h('div', { class: 'row' }, h('h3', { text: `${r.data.total} visitors` }), h('span', { class: 'grow' }), h('label', {}, 'Group by ', groupSel), sortSel),
      h('p', { class: 'muted', text: state.group === 'cookie' ? 'Each browser gets an anonymous id cookie, so people sharing an IP address are told apart. Older records without a cookie are grouped by IP + browser.' : state.group === 'browser' ? 'Same IP address, different browsers or devices are shown as different visitors.' : 'Everything from one IP address is one visitor.' }),
      rows.length ? h('div', { class: 'scroll' }, h('table', {},
        h('tr', {}, ['Visitor', 'Country', 'Last seen', 'First seen', 'Events', 'Photo views', 'Downloads', 'Galleries'].map(x => h('th', { text: x }))),
        rows.map(v => h('tr', {}, h('td', {}, h('a', { href: visitorHref(v.key), text: state.group === 'ip' ? v.ip : uaLabel(v.userAgent) + (v.isBot ? ' (bot)' : '') }), h('div', { class: 'muted', title: v.userAgent, text: v.ips.join(', ') + (v.ipCount > v.ips.length ? ` +${v.ipCount - v.ips.length}` : '') + (state.group === 'ip' && v.browserCount > 1 ? ` \u00B7 ${v.browserCount} browsers` : '') + (state.group === 'cookie' && !v.cookie ? ' \u00B7 no cookie' : '') })), h('td', { text: v.country || '-' }),
          h('td', { text: ago(v.last), title: new Date(v.last).toLocaleString() }), h('td', { text: new Date(v.first).toLocaleDateString() }),
          h('td', { text: v.events }), h('td', { text: v.photoViews }), h('td', { text: v.downloads }),
          h('td', { text: v.galleries.join(', ') }))))) : h('p', { class: 'muted', text: 'No visitors in this period.' })));
  };

  const loadVisitor = async () => {
    tabs.replaceChildren();
    document.title = 'Visitor ' + state.ip.replace(/^[a-z]+:/, '').split('|')[0] + ' - ' + site.title;
    const p = new URLSearchParams({ key: state.ip, group: state.group, days: state.days }); if (state.gallery) p.set('gallery', state.gallery);
    const r = await api('/api/admin/stats/visitor?' + p);
    const back = h('button', { text: '\u2190 All visitors', onclick: () => { state.ip = ''; state.view = 'visitors'; load(); } });
    if (!r.ok) { body.replaceChildren(back, h('p', { class: 'muted', text: 'No activity from this IP address in the selected period.' })); return; }
    const d = r.data, s = d.summary;
    const tile = (n, label) => h('div', { class: 'tile' }, h('div', { class: 'tile-n', text: String(n) }), h('div', { class: 'muted', text: label }));
    const when = (a, b) => { const x = new Date(a), y = new Date(b); const day = x.toLocaleDateString() === y.toLocaleDateString(); return `${x.toLocaleString()} \u2013 ${day ? y.toLocaleTimeString() : y.toLocaleString()}`; };
    body.replaceChildren(
      h('div', { class: 'toolbar' }, back, h('h2', { text: uaLabel(d.browsers[0] && d.browsers[0].userAgent) + ' \u00B7 ' + s.ip }), s.isBot ? h('span', { class: 'chip warn', text: 'bot / crawler' }) : null, s.country ? h('span', { class: 'chip', text: s.country }) : null, h('span', { class: 'muted', text: `First seen ${new Date(s.first).toLocaleString()} \u00B7 last seen ${new Date(s.last).toLocaleString()}` })),
      d.neighbours.length ? h('div', { class: 'panel' }, h('h3', { text: 'Others on the same IP address' }), h('p', { class: 'muted', text: 'Other visitors seen from the same address, for example the rest of the household.' }),
        h('table', {}, h('tr', {}, ['Visitor', 'IP', 'Events', 'Last seen'].map(x => h('th', { text: x }))), d.neighbours.map(n => h('tr', {}, h('td', {}, h('a', { href: visitorHref(n.key), text: uaLabel(n.userAgent) + (n.cookie ? '' : ' (no cookie)') })), h('td', { text: n.ip }), h('td', { text: n.events }), h('td', { text: new Date(n.last).toLocaleString() }))))) : '',
      h('div', { class: 'tiles' }, tile(s.events, 'Events'), tile(s.galleryViews, 'Gallery views'), tile(s.photoViews, 'Photo views'), tile(s.downloads, 'Photo downloads'), tile(s.zips, 'ZIP downloads'), tile(s.unlockFails, 'Wrong passwords'), tile(s.uploads, 'Uploads')),
      h('div', { class: 'panel' }, h('h3', { text: `Visits (${d.sessions.length}${d.sessions.length === 30 ? '+' : ''})` }),
        h('p', { class: 'muted', text: 'A new visit starts after 30 minutes without activity.' }),
        d.sessions.map(v => h('div', { class: 'session' },
          h('div', { class: 'session-head' }, h('b', { text: when(v.start, v.end) }), h('span', { class: 'muted', text: ` \u00B7 ${v.events} events \u00B7 ${v.photoViews} photo views \u00B7 ${v.downloads} downloads` })),
          h('div', { class: 'muted', text: v.galleries.length ? 'Galleries: ' + v.galleries.join(', ') : '' }),
          v.photoIds.length ? h('div', { class: 'thumbs' }, v.photoIds.map(id => h('img', { src: `/img/${id}/thumb`, alt: '', class: 'tiny', loading: 'lazy' }))) : null))),
      h('div', { class: 'two' },
        h('div', { class: 'panel' }, h('h3', { text: 'Galleries visited' }), d.galleries.length ? h('table', {},
          h('tr', {}, ['Gallery', 'Views', 'Photo views', 'Downloads', 'Last'].map(x => h('th', { text: x }))),
          d.galleries.map(g => h('tr', {}, h('td', { text: g.title }), h('td', { text: g.views }), h('td', { text: g.photoViews }), h('td', { text: g.downloads }), h('td', { text: new Date(g.last).toLocaleString() })))) : h('p', { class: 'muted', text: 'None.' })),
        h('div', { class: 'panel' }, h('h3', { text: 'Browsers and IP addresses' }), h('table', {}, h('tr', {}, ['Browser', 'Events'].map(x => h('th', { text: x }))),
          d.browsers.map(b => h('tr', {}, h('td', { class: 'ua2', text: b.userAgent || '(none)' }), h('td', { text: b.events })))),
          h('table', {}, h('tr', {}, ['IP address', 'Events', 'Last'].map(x => h('th', { text: x }))),
          d.ips.map(i => h('tr', {}, h('td', { text: i.ip + (i.country ? ' (' + i.country + ')' : '') }), h('td', { text: i.events }), h('td', { text: new Date(i.last).toLocaleString() })))))),
      h('div', { class: 'panel' }, h('h3', { text: `Photos looked at (${d.photos.length})` }), d.photos.length ? h('div', { class: 'photo-cards' }, d.photos.map(ph =>
        h('div', { class: 'photo-card' }, h('img', { src: `/img/${ph.id}/thumb`, alt: ph.name, loading: 'lazy' }),
          h('div', { class: 'pc-name', text: ph.name, title: ph.name }),
          h('div', { class: 'muted', text: ph.gallery }),
          h('div', { class: 'muted', text: `${ph.views} view${ph.views === 1 ? '' : 's'}${ph.downloads ? ` \u00B7 ${ph.downloads} download${ph.downloads === 1 ? '' : 's'} (${ph.downloaded.join(', ')})` : ''}` })))) : h('p', { class: 'muted', text: 'No photo views.' })),
      h('details', { class: 'section' }, h('summary', {}, h('span', { class: 'section-title', text: 'Full timeline' }), h('span', { class: 'muted', text: `latest ${d.timeline.length} events` })),
        h('div', { class: 'section-body scroll' }, h('table', {}, h('tr', {}, ['Time', 'Action', 'Gallery', 'Photo / detail'].map(x => h('th', { text: x }))),
          d.timeline.map(e => h('tr', {}, h('td', { text: new Date(e.time).toLocaleString() }), h('td', { text: kindLabels[e.kind] || e.kind }), h('td', { text: e.gallery }), h('td', { text: [e.photo, e.detail].filter(Boolean).join(' - ') })))))));
  };

  const backupPanel = () => {
    const box = h('div', { class: 'panel' });
    const load = async () => {
      const r = await api('/api/admin/backups');
      const title = h('h3', { text: 'Database backups' });
      if (!r.ok) { box.replaceChildren(title, h('p', { class: 'err', text: 'Could not load the backup list.' })); return; }
      const b = r.data;
      const newest = b.snapshots[0];
      const stale = newest && (Date.now() - new Date(newest.time).getTime()) > b.everyHours * 1.5 * 3600000;
      const msg = h('span', { class: 'muted' });
      const btn = h('button', { text: 'Back up now' });
      btn.addEventListener('click', async () => {
        btn.disabled = true; msg.textContent = 'Working...';
        const x = await api('/api/admin/backups/run', { method: 'POST' });
        if (x.ok) load(); else { btn.disabled = false; msg.textContent = typeof x.data === 'object' && x.data ? x.data.detail || 'Backup failed.' : 'Backup failed.'; }
      });
      const summary = !b.enabled ? 'Automatic backups are off (BACKUP_HOURS=0).'
        : !newest ? 'No backup has been made yet. The first one is taken a few minutes after the app starts.'
        : `Last backup ${ago(newest.time)} (${fmtBytes(newest.bytes)}). A snapshot is taken every ${b.everyHours} h; the last ${b.keepDaily} days and ${b.keepWeekly} older weeks are kept.`;
      box.replaceChildren(title,
        h('p', { class: stale || b.lastError ? 'err' : 'muted', text: b.lastError ? `The last attempt failed: ${b.lastError}` : summary }),
        b.snapshots.length ? h('table', {}, h('tr', {}, ['Taken', 'Size', 'File'].map(x => h('th', { text: x }))),
          b.snapshots.map(x => h('tr', {}, h('td', { text: new Date(x.time).toLocaleString() }), h('td', { text: fmtBytes(x.bytes) }), h('td', { class: 'muted', text: x.name })))) : null,
        h('p', {}, btn, ' ', msg));
    };
    box.append(h('h3', { text: 'Database backups' }), h('p', { class: 'muted', text: 'Loading...' }));
    load();
    return box;
  };
  const activityPanel = () => {
    const box = h('div', { class: 'panel' }, h('h3', { text: 'Recent activity' }));
    const kindSel = h('select', {}, [h('option', { value: '', text: 'All actions' }), ...Object.entries(kindLabels).map(([v, t]) => h('option', { value: v, text: t, selected: v === state.kind }))]);
    const table = h('table', {});
    const more = h('button', { text: 'Load more' });
    const info = h('span', { class: 'muted' });
    let skip = 0;
    const fetchPage = async (reset) => {
      if (reset) { skip = 0; table.replaceChildren(h('tr', {}, ['Time', 'IP address', 'Country', 'Action', 'Gallery', 'Photo / detail', 'Browser'].map(x => h('th', { text: x })))); }
      const p = q(); p.set('skip', skip); p.set('take', 50); if (state.kind) p.set('kind', state.kind);
      const r = await api('/api/admin/stats/activity?' + p);
      if (!r.ok) return;
      for (const it of r.data.items) {
        table.append(h('tr', {}, h('td', { text: new Date(it.time).toLocaleString() }), h('td', {}, it.ip ? h('a', { href: '/admin/stats?visitor=' + encodeURIComponent(it.visitorId ? 'c:' + it.visitorId : 'ip:' + it.ip) + '&group=' + (it.visitorId ? 'cookie' : 'ip') + '&days=' + state.days, text: it.ip }) : '-'), h('td', { text: it.country || '-' }),
          h('td', { text: kindLabels[it.kind] || it.kind }), h('td', { text: it.gallery }), h('td', { text: [it.photo, it.detail].filter(Boolean).join(' - ') }),
          h('td', { class: 'ua', title: it.userAgent, text: (it.isBot ? '[bot] ' : '') + it.userAgent })));
      }
      skip += r.data.items.length;
      info.textContent = `Showing ${Math.min(skip, r.data.total)} of ${r.data.total}`;
      more.style.display = skip < r.data.total ? '' : 'none';
    };
    kindSel.onchange = () => { state.kind = kindSel.value; fetchPage(true); };
    more.onclick = () => fetchPage(false);
    box.append(h('div', { class: 'row' }, kindSel, info), h('div', { class: 'scroll' }, table), more);
    fetchPage(true);
    return box;
  };

  rangeSel.onchange = () => { state.days = Number(rangeSel.value); load(); };
  galSel.onchange = () => { state.gallery = galSel.value; load(); };
  app.append(h('div', { class: 'toolbar' }, h('h1', { text: 'Statistics' }), h('span', { class: 'grow' }), rangeSel, galSel, h('a', { class: 'btn', href: '/admin/settings/visitor-stats', text: 'Tracking settings' })),
    viewTabs, tabs, body);
  load();
}

// ---------- boot ----------
(async function boot() {
  try { const r = await api('/api/site'); if (r.ok) site = r.data; } catch { }
  try { const r = await api('/api/me'); if (r.ok) me = r.data; } catch { }
  try { const r = await api('/api/license'); if (r.ok) license = r.data; } catch { }
  if (new URLSearchParams(location.search).get('signin') === 'failed') { signinFailed = true; history.replaceState(null, '', location.pathname); }
  const qp = new URLSearchParams(location.search);
  if (qp.has('subscribed') || qp.has('unsubscribed')) {
    toast(qp.has('unsubscribed') ? 'You have been unsubscribed.' : qp.get('subscribed') === '1' ? 'Subscribed. You will get emails about new galleries and photos.' : 'That link is not valid.');
    history.replaceState(null, '', location.pathname);
  }
  syncAccountTheme();
  applyTheme();
  renderHeader();
  renderFooter();
  route();
})();
