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
const fmtLabels = { png: 'PNG (lossless)', webp: 'WebP (lossless)', tif: 'TIFF (lossless)' };
let site = { title: 'Photos', sizes: [], formats: [] };
const fmtDate = (s) => s ? new Date(/Z$|[+-]\d\d:\d\d$/.test(s) ? s : s + 'Z').toLocaleString() : '';

// ---------- routing ----------
let dirtyGuard = null; // set by pages with unsaved changes: returns true when it is OK to leave
let galleryView = null; // slug of the gallery grid currently rendered, so photo URLs can open/close the viewer without re-rendering
async function route(force = false) {
  dirtyGuard = null;
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
  if (parts[0] === 'admin' && parts[1] === 'settings') return viewSettings(app);
  if (parts[0] === 'admin' && parts[1] === 'galleries' && parts[2]) return viewGallerySettings(app, Number(parts[2]));
  if (parts[0] === 'admin') return viewAdmin(app);
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
  if (href === '/' || /^\/(g|admin|expired)(\/|$)/.test(href)) {
    e.preventDefault();
    if (dirtyGuard && !dirtyGuard()) return;
    go(href);
  }
});
function go(path) { if (path !== location.pathname) history.pushState(null, '', path); route(); window.scrollTo(0, 0); }

function renderHeader() {
  const top = $('#top'); top.replaceChildren(
    h('a', { class: 'brand', href: '/', text: site.title }),
    h('nav', {}, h('a', { href: '/admin', text: 'Admin' })));
}

function eventLine(g) {
  const d = g.eventDate ? new Date(/Z$|[+-]\d\d:\d\d$/.test(g.eventDate) ? g.eventDate : g.eventDate + 'Z').toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric', timeZone: 'UTC' }) : '';
  return [d, g.location].filter(Boolean).join(' \u00B7 ');
}

// ---------- home ----------
async function viewHome(app) {
  const r = await api('/api/galleries');
  app.append(h('h1', { text: site.title }), h('p', { class: 'sub', text: 'Public galleries' }));
  if (!r.ok || !r.data.length) return app.append(h('p', { class: 'muted', text: 'No public galleries yet. Private galleries are reached through their share link.' }));
  app.append(h('div', { class: 'cards' }, r.data.map(g =>
    h('a', { class: 'card', href: '/g/' + encodeURIComponent(g.slug) },
      g.cover ? h('img', { class: 'cover', loading: 'lazy', src: `/img/${g.cover}/thumb`, alt: '' }) : h('div', { class: 'cover' }),
      h('div', { class: 'meta' }, h('b', { text: g.title }), g.eventDate || g.location ? h('span', { class: 'muted', text: eventLine(g) }) : null, h('span', { class: 'muted', text: `${g.count} photos` }))))));
}

// ---------- gallery ----------
let G = null; // current gallery payload

async function viewGallery(app, slug) {
  const r = await api('/api/g/' + encodeURIComponent(slug));
  if (r.status === 401 && r.data && r.data.needsPassword) return passwordPrompt(app, slug, r.data.title);
  if (r.status === 410) return app.append(h('div', { class: 'center' }, h('h1', { text: (r.data && r.data.title) || 'Gallery closed' }), h('p', { class: 'sub', text: 'This gallery is no longer available.' })));
  if (!r.ok) return app.append(h('div', { class: 'center' }, h('h1', { text: 'Not available' }), h('p', { class: 'sub', text: 'This gallery does not exist or you do not have access.' })));
  G = r.data;
  document.title = G.title + ' - ' + site.title;
  app.append(h('h1', { text: G.title }));
  if (G.eventDate || G.location) app.append(h('p', { class: 'muted', text: eventLine(G) }));
  if (G.description) app.append(h('p', { class: 'sub', text: G.description }));
  if (G.link) {
    const bits = [];
    if (G.link.expires) bits.push('link expires ' + fmtDate(G.link.expires));
    if (G.link.remaining !== null && G.link.remaining !== undefined) bits.push(G.link.remaining + ' downloads remaining');
    if (bits.length) app.append(h('p', { class: 'muted', text: bits.join(' · ') }));
  }

  const approved = G.photos.filter(p => p.approved || G.admin);
  const rawOnlyCount = approved.filter(p => p.rawOnly).length;
  const tb = h('div', { class: 'toolbar' });
  const countLabel = h('span', { class: 'muted' });
  const slideBtn = h('button', { class: 'primary', text: '\u25B6 Slideshow', onclick: () => openLightbox(0, true) });
  const curated = h('input', { type: 'checkbox', checked: G.hideRawOnly !== false });
  const curatedText = h('span');
  const rawBtn = h('label', { class: 'switch', title: 'Curated photos are the edited ones. Turn this off to also see unedited RAW frames.' }, curated, curatedText);
  const grid = h('div', { class: 'grid' });
  const fill = () => {
    const showRaw = !curated.checked;
    const photos = showRaw ? approved : approved.filter(p => !p.rawOnly);
    G.list = photos;
    countLabel.textContent = `${photos.filter(p => p.approved).length} photos`;
    curatedText.textContent = showRaw ? ' Curated only' : ` Curated only (${rawOnlyCount} unedited RAW hidden)`;
    slideBtn.disabled = !photos.length;
    grid.replaceChildren(...photos.map((p, i) => {
      const ratio = (p.w || 3) / (p.h || 2);
      const item = h('div', { class: 'ph' + (p.approved ? '' : ' pending'), title: p.name, onclick: () => openLightbox(i, false) },
        h('img', { loading: 'lazy', src: `/img/${p.id}/thumb`, alt: p.name }),
        p.approved ? null : h('span', { class: 'badge', text: 'pending' }),
        p.rawOnly ? h('span', { class: 'badge', text: 'RAW' }) : null);
      item.style.width = (ratio * 220) + 'px';
      item.style.flexGrow = String(ratio * 100);
      item.style.aspectRatio = String(ratio);
      item.style.minWidth = '120px';
      return item;
    }));
  };
  curated.addEventListener('change', fill);
  tb.append(countLabel, slideBtn);
  if (rawOnlyCount > 0) tb.append(rawBtn);
  tb.append(h('span', { class: 'grow' }));
  if (G.allowZip) tb.append(zipControls());
  if (G.allowUpload) tb.append(uploadControl(slug));
  app.append(tb, grid);
  fill();
  G.revealAll = () => { curated.checked = false; fill(); };
  galleryView = slug;
}

function passwordPrompt(app, slug, title) {
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

function sizeSelect(allowOriginal) {
  const s = h('select');
  if (allowOriginal) s.append(h('option', { value: 'original', text: 'Original file' }));
  for (const px of (G ? G.sizes : site.sizes)) s.append(h('option', { value: px, text: `${px}px long edge` }));
  return s;
}
function fmtSelect() {
  const s = h('select');
  for (const f of (G ? G.formats : site.formats)) s.append(h('option', { value: f, text: fmtLabels[f] || f }));
  return s;
}

function zipControls() {
  const size = sizeSelect(G.allowOriginal), fmt = fmtSelect();
  const rawBox = h('input', { type: 'checkbox' });
  const sync = () => { fmt.disabled = size.value === 'original'; };
  size.addEventListener('change', sync); sync();
  const btn = h('button', { text: 'Download ZIP', onclick: () => {
    const q = new URLSearchParams({ size: size.value, fmt: fmt.value });
    if (rawBox.checked) q.set('raw', 'true');
    location.href = `/api/g/${encodeURIComponent(G.slug)}/zip?${q}`;
  } });
  return h('div', { class: 'dlbar' }, size, fmt, G.allowRaw ? h('label', {}, rawBox, ' + RAW') : null, btn);
}

function photoDownloadControls(p) {
  if (!G.allowDownload) return null;
  const size = sizeSelect(G.allowOriginal && !p.rawOnly), fmt = fmtSelect();
  const sync = () => { fmt.disabled = size.value === 'original'; };
  size.addEventListener('change', sync); sync();
  const go = h('button', { text: 'Download', onclick: () => {
    const q = new URLSearchParams({ size: size.value, fmt: fmt.value });
    location.href = `/dl/${p.id}?${q}`;
  } });
  const bar = h('div', { class: 'dlbar' }, size, fmt, go);
  if (G.allowRaw && p.raw) bar.append(h('a', { class: 'btn', href: `/dl/${p.id}/raw`, text: 'RAW' }));
  return bar;
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
  const root = $('#lightbox'); root.classList.remove('hidden', 'idle'); root.replaceChildren();
  document.body.style.overflow = 'hidden';
  const img = h('img', { alt: '' });
  const counter = h('span', { class: 'muted' });
  const title = h('span', { text: '' });
  const dl = h('div');
  const playBtn = h('button', { text: '▶ Play' });
  const speed = h('select', {}, [3, 5, 8, 12].map(s => h('option', { value: s, text: s + 's', selected: s === 5 })));
  const fsBtn = h('button', { text: 'Fullscreen' });
  const closeBtn = h('button', { text: 'Close ✕' });
  const prev = h('button', { class: 'nav prev', text: '‹', 'aria-label': 'Previous' });
  const next = h('button', { class: 'nav next', text: '›', 'aria-label': 'Next' });
  root.append(
    h('div', { class: 'bar top' }, h('div', { class: 'row' }, title, counter), h('div', { class: 'row' }, playBtn, speed, fsBtn, closeBtn)),
    h('div', { class: 'stage' }, prev, img, next),
    h('div', { class: 'bar' }, dl));
  lb = { i: index, timer: null, idle: null, playing: false };

  const show = (i, updateUrl = true) => {
    lb.i = (i + list.length) % list.length;
    const p = list[lb.i];
    if (updateUrl) history.replaceState(history.state, '', photoPath(p));
    img.src = `/img/${p.id}/view`;
    title.textContent = p.name; counter.textContent = `${lb.i + 1} / ${list.length}`;
    const unlink = G.admin && p.raw && !p.rawOnly
      ? h('button', { class: 'danger', text: 'Unlink RAW', onclick: async () => { if (confirm('Unlink the RAW from this photo?')) { await api(`/api/admin/photos/${p.id}/link-raw`, { method: 'DELETE' }); route(true); } } })
      : null;
    const isCover = G.cover === p.id;
    const coverBtn = G.admin && p.approved
      ? h('button', { text: isCover ? '★ Gallery cover' : 'Set as gallery cover', disabled: isCover, onclick: async () => {
          const r = await api(`/api/admin/galleries/${G.id}/cover`, { method: 'PUT', body: { photoId: p.id } });
          if (r.ok) { G.cover = p.id; toast('Gallery cover updated'); show(lb.i); } else toast('Could not set cover');
        } })
      : null;
    dl.replaceChildren(photoDownloadControls(p) || '', coverBtn || '', unlink || '');
    const n = list[(lb.i + 1) % list.length]; new Image().src = `/img/${n.id}/view`; // preload
  };
  const stop = () => { lb.playing = false; clearInterval(lb.timer); playBtn.textContent = '▶ Play'; };
  const start = () => { lb.playing = true; playBtn.textContent = '❚❚ Pause'; clearInterval(lb.timer); lb.timer = setInterval(() => show(lb.i + 1), Number(speed.value) * 1000); };
  playBtn.onclick = () => lb.playing ? stop() : start();
  speed.onchange = () => { if (lb.playing) start(); };
  prev.onclick = () => show(lb.i - 1); next.onclick = () => show(lb.i + 1);
  lb.show = show;
  closeBtn.onclick = requestClose;
  fsBtn.onclick = () => document.fullscreenElement ? document.exitFullscreen() : root.requestFullscreen?.();
  const wake = () => { root.classList.remove('idle'); clearTimeout(lb.idle); lb.idle = setTimeout(() => lb.playing && root.classList.add('idle'), 3000); };
  root.onmousemove = wake; root.ontouchstart = wake;
  lb.key = (e) => {
    if (e.target.tagName === 'SELECT') return;
    if (e.key === 'Escape') requestClose();
    else if (e.key === 'ArrowRight') show(lb.i + 1);
    else if (e.key === 'ArrowLeft') show(lb.i - 1);
    else if (e.key === ' ') { e.preventDefault(); lb.playing ? stop() : start(); }
    else if (e.key === 'f') fsBtn.click();
    wake();
  };
  document.addEventListener('keydown', lb.key);
  let tx = null;
  root.addEventListener('touchstart', e => { tx = e.touches[0].clientX; }, { passive: true });
  root.addEventListener('touchend', e => { if (tx === null) return; const dx = e.changedTouches[0].clientX - tx; if (Math.abs(dx) > 50) show(lb.i + (dx < 0 ? 1 : -1)); tx = null; });
  show(index, false); if (play) { start(); } wake();
}
function closeLightbox() {
  const root = $('#lightbox');
  if (!lb) return;
  clearInterval(lb.timer); clearTimeout(lb.idle);
  document.removeEventListener('keydown', lb.key);
  if (document.fullscreenElement) document.exitFullscreen();
  root.classList.add('hidden'); root.replaceChildren(); document.body.style.overflow = ''; lb = null;
}

// ---------- admin ----------
async function viewAdmin(app) {
  const me = await api('/api/admin/me');
  if (!me.data || !me.data.admin) return adminLogin(app);
  const [gals, folders] = await Promise.all([api('/api/admin/galleries'), api('/api/admin/folders')]);
  app.append(h('div', { class: 'toolbar' }, h('h1', { text: 'Admin' }), h('span', { class: 'grow' }),
    h('a', { class: 'btn', href: '/admin/settings', text: 'Site settings' }),
    h('button', { text: 'Log out', onclick: async () => { await api('/api/admin/logout', { method: 'POST' }); route(); } })));
  app.append(newGalleryPanel((folders.data || []).filter(f => !f.galleryId).map(f => f.path)));
  const list = gals.data || [];
  app.append(h('h2', { text: `Galleries (${list.length})` }));
  if (!list.length) return app.append(h('p', { class: 'muted', text: 'No galleries yet. Create one from a folder above.' }));
  app.append(h('div', { class: 'gal-list' }, list.map(galleryRow)));
}

const visShort = { 0: 'Public', 1: 'Unlisted', 2: 'Protected' };
function galleryRow(g) {
  const settingsHref = '/admin/galleries/' + g.id;
  const expired = g.expiresAt && new Date(/Z$/.test(g.expiresAt) ? g.expiresAt : g.expiresAt + 'Z') <= new Date();
  return h('div', { class: 'gal-row' },
    h('a', { href: settingsHref, class: 'gal-thumb' }, g.cover ? h('img', { src: `/img/${g.cover}/thumb`, alt: '', loading: 'lazy' }) : h('span', { class: 'muted', text: 'No cover' })),
    h('div', { class: 'gal-info' },
      h('a', { href: settingsHref, class: 'gal-name', text: g.title }),
      h('div', { class: 'chips' },
        h('span', { class: 'chip v' + g.visibility, text: visShort[g.visibility] }),
        h('span', { class: 'chip', text: `${g.count} photos` }),
        g.pending ? h('span', { class: 'chip warn', text: `${g.pending} pending` }) : null,
        expired ? h('span', { class: 'chip warn', text: 'Expired' }) : null,
        g.hasPassword ? h('span', { class: 'chip', text: 'Password' }) : null),
      h('div', { class: 'muted', text: '/g/' + g.slug })),
    h('div', { class: 'gal-actions' },
      h('a', { class: 'btn', href: '/g/' + encodeURIComponent(g.slug), text: 'View' }),
      h('a', { class: 'btn primary', href: settingsHref, text: 'Settings' })));
}

function adminLogin(app) {
  const input = h('input', { type: 'password', placeholder: 'Admin password', autocomplete: 'current-password' });
  const err = h('div', { class: 'err' });
  const go = async () => {
    const r = await api('/api/admin/login', { method: 'POST', body: { password: input.value } });
    if (r.ok) route(); else err.textContent = r.status === 429 ? 'Too many attempts, wait a minute.' : 'Incorrect password.';
  };
  input.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
  app.append(h('div', { class: 'center' }, h('h1', { text: 'Admin' }), input, h('button', { class: 'primary', text: 'Sign in', onclick: go }), err));
  input.focus();
}

const visLabels = { 0: 'Public (listed on home page)', 1: 'Unlisted (anyone with the link)', 2: 'Protected (password or share link)' };

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

async function viewSettings(app) {
  const me = await api('/api/admin/me');
  if (!me.data || !me.data.admin) return adminLogin(app);
  const r = await api('/api/admin/settings');
  const w = r.data.watermark;
  let hasPng = r.data.hasPng;
  app.append(h('div', { class: 'toolbar' }, h('h1', { text: 'Settings' }), h('span', { class: 'grow' }), h('a', { class: 'btn', href: '/admin', text: 'Back to admin' })));

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
  app.append(cachePanel());
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
  const me = await api('/api/admin/me');
  if (!me.data || !me.data.admin) return adminLogin(app);
  const gals = await api('/api/admin/galleries');
  const g = (gals.data || []).find(x => x.id === id);
  if (!g) return app.append(h('div', { class: 'center' }, h('h1', { text: 'Gallery not found' }), h('a', { class: 'btn', href: '/admin', text: 'Back to admin' })));
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
  const general = section('General', 'Name, date, order, cover', true,
    h('div', { class: 'form1' },
      field('Title', title), field('URL name', slug, `Gallery address: /g/${g.slug}`), field('Description', desc),
      field('Event date', eventDate), field('Location', location_),
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
        dirty = false; await api('/api/admin/galleries/' + g.id, { method: 'DELETE' }); go('/admin');
      } }))));

  // --- Access & sharing ---
  const vis = track(h('select', {}, Object.entries(visLabels).map(([v, t]) => h('option', { value: v, text: t, selected: Number(v) === g.visibility }))));
  const pw = track(h('input', { type: 'password', placeholder: g.hasPassword ? 'Password set (type to change, - to remove)' : 'No password', autocomplete: 'new-password' }));
  const expires = track(h('input', { type: 'datetime-local', value: toLocalInput(g.expiresAt) }));
  const access = section('Access & sharing', 'Who can open it, passwords, links', true,
    h('div', { class: 'form1' },
      field('Visibility', vis, 'Public shows on the home page. Unlisted needs the link. Protected needs a password or a share link.'),
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
    h('p', {}, h('a', { href: '/admin/settings', text: 'Open site settings (watermark style)' })), pendingManager(g));

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
      rawMatchMode: Number(rawMode.value), allowRaw: allowRaw.checked, hideRawOnly: hideRaw.checked
    };
    if (pw.value) body.password = pw.value === '-' ? '' : pw.value;
    const r = await api('/api/admin/galleries/' + g.id, { method: 'PUT', body });
    if (r.ok) { dirty = false; toast('Saved'); route(); }
    else status.textContent = typeof r.data === 'string' ? r.data : 'Save failed';
  };

  app.append(
    h('div', { class: 'toolbar' }, h('a', { class: 'btn', href: '/admin', text: '\u2190 All galleries' }), h('h1', { text: g.title }), h('span', { class: 'grow' }),
      h('a', { class: 'btn', href: '/g/' + encodeURIComponent(g.slug), text: 'View gallery' })),
    general, access, downloads, wmUp, raw,
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

// ---------- boot ----------
(async function boot() {
  try { const r = await api('/api/site'); if (r.ok) site = r.data; } catch { }
  renderHeader();
  route();
})();
