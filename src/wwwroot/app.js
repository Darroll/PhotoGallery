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
const fmtLabels = { png: 'PNG (lossless)', webp: 'WebP (lossless)', tif: 'TIFF (lossless)' };
let site = { title: 'Photos', sizes: [], formats: [] };
const fmtDate = (s) => s ? new Date(/Z$|[+-]\d\d:\d\d$/.test(s) ? s : s + 'Z').toLocaleString() : '';

// ---------- routing ----------
async function route() {
  closeLightbox();
  const hash = location.hash.replace(/^#/, '') || '/';
  const parts = hash.split('/').filter(Boolean);
  const app = $('#app'); app.replaceChildren();
  document.title = site.title;
  if (parts[0] === 'g' && parts[1]) return viewGallery(app, decodeURIComponent(parts[1]));
  if (parts[0] === 'admin' && parts[1] === 'settings') return viewSettings(app);
  if (parts[0] === 'admin') return viewAdmin(app);
  if (parts[0] === 'expired') return app.append(h('div', { class: 'center' }, h('h1', { text: 'Link unavailable' }), h('p', { class: 'sub', text: 'This share link has expired or was revoked.' })));
  return viewHome(app);
}
window.addEventListener('hashchange', route);

function renderHeader() {
  const top = $('#top'); top.replaceChildren(
    h('a', { class: 'brand', href: '#/', text: site.title }),
    h('nav', {}, h('a', { href: '#/admin', text: 'Admin' })));
}

// ---------- home ----------
async function viewHome(app) {
  const r = await api('/api/galleries');
  app.append(h('h1', { text: site.title }), h('p', { class: 'sub', text: 'Public galleries' }));
  if (!r.ok || !r.data.length) return app.append(h('p', { class: 'muted', text: 'No public galleries yet. Private galleries are reached through their share link.' }));
  app.append(h('div', { class: 'cards' }, r.data.map(g =>
    h('a', { class: 'card', href: '#/g/' + encodeURIComponent(g.slug) },
      g.cover ? h('img', { class: 'cover', loading: 'lazy', src: `/img/${g.cover}/thumb`, alt: '' }) : h('div', { class: 'cover' }),
      h('div', { class: 'meta' }, h('b', { text: g.title }), h('span', { class: 'muted', text: `${g.count} photos` }))))));
}

// ---------- gallery ----------
let G = null; // current gallery payload

async function viewGallery(app, slug) {
  const r = await api('/api/g/' + encodeURIComponent(slug));
  if (r.status === 401 && r.data && r.data.needsPassword) return passwordPrompt(app, slug, r.data.title);
  if (!r.ok) return app.append(h('div', { class: 'center' }, h('h1', { text: 'Not available' }), h('p', { class: 'sub', text: 'This gallery does not exist or you do not have access.' })));
  G = r.data;
  document.title = G.title + ' - ' + site.title;
  app.append(h('h1', { text: G.title }));
  if (G.description) app.append(h('p', { class: 'sub', text: G.description }));
  if (G.link) {
    const bits = [];
    if (G.link.expires) bits.push('link expires ' + fmtDate(G.link.expires));
    if (G.link.remaining !== null && G.link.remaining !== undefined) bits.push(G.link.remaining + ' downloads remaining');
    if (bits.length) app.append(h('p', { class: 'muted', text: bits.join(' · ') }));
  }

  const approved = G.photos.filter(p => p.approved || G.admin);
  const rawOnlyCount = approved.filter(p => p.rawOnly).length;
  let showRaw = false;
  const tb = h('div', { class: 'toolbar' });
  const countLabel = h('span', { class: 'muted' });
  const slideBtn = h('button', { class: 'primary', text: '\u25B6 Slideshow', onclick: () => openLightbox(0, true) });
  const rawBtn = h('button', { title: 'RAW frames without an edited version are hidden by default' });
  const grid = h('div', { class: 'grid' });
  const fill = () => {
    const photos = showRaw ? approved : approved.filter(p => !p.rawOnly);
    G.list = photos;
    countLabel.textContent = `${photos.filter(p => p.approved).length} photos`;
    rawBtn.textContent = showRaw ? 'Hide RAW-only photos' : `Show RAW-only photos (${rawOnlyCount})`;
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
  rawBtn.onclick = () => { showRaw = !showRaw; fill(); };
  tb.append(countLabel, slideBtn);
  if (rawOnlyCount > 0) tb.append(rawBtn);
  tb.append(h('span', { class: 'grow' }));
  if (G.allowZip) tb.append(zipControls());
  if (G.allowUpload) tb.append(uploadControl(slug));
  app.append(tb, grid);
  fill();
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
  for (const px of site.sizes) s.append(h('option', { value: px, text: `${px}px long edge` }));
  return s;
}
function fmtSelect() {
  const s = h('select');
  for (const f of site.formats) s.append(h('option', { value: f, text: fmtLabels[f] || f }));
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
    if (ok && !G.uploadsModerated) route();
  });
  return h('div', { class: 'dlbar' }, h('button', { text: 'Upload photos', onclick: () => file.click() }), file, label);
}

// ---------- lightbox / slideshow ----------
let lb = null;
function openLightbox(index, play) {
  const list = G.list; if (!list.length) return;
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

  const show = (i) => {
    lb.i = (i + list.length) % list.length;
    const p = list[lb.i];
    img.src = `/img/${p.id}/view`;
    title.textContent = p.name; counter.textContent = `${lb.i + 1} / ${list.length}`;
    const unlink = G.admin && p.raw && !p.rawOnly
      ? h('button', { class: 'danger', text: 'Unlink RAW', onclick: async () => { if (confirm('Unlink the RAW from this photo?')) { await api(`/api/admin/photos/${p.id}/link-raw`, { method: 'DELETE' }); route(); } } })
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
  closeBtn.onclick = closeLightbox;
  fsBtn.onclick = () => document.fullscreenElement ? document.exitFullscreen() : root.requestFullscreen?.();
  const wake = () => { root.classList.remove('idle'); clearTimeout(lb.idle); lb.idle = setTimeout(() => lb.playing && root.classList.add('idle'), 3000); };
  root.onmousemove = wake; root.ontouchstart = wake;
  lb.key = (e) => {
    if (e.target.tagName === 'SELECT') return;
    if (e.key === 'Escape') closeLightbox();
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
  show(index); if (play) { start(); } wake();
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
    h('a', { class: 'btn', href: '#/admin/settings', text: 'Settings' }),
    h('button', { text: 'Log out', onclick: async () => { await api('/api/admin/logout', { method: 'POST' }); route(); } })));
  app.append(newGalleryPanel(folders.data || []));
  const list = gals.data || [];
  if (!list.length) app.append(h('p', { class: 'muted', text: 'No galleries yet. Create one from a folder above.' }));
  for (const g of list) app.append(galleryPanel(g));
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
  const title = h('input', { type: 'text', placeholder: 'Gallery title' });
  const folder = h('select', {}, folders.map(f => h('option', { value: f, text: f })));
  const vis = h('select', {}, Object.entries(visLabels).map(([v, t]) => h('option', { value: v, text: t, selected: v === '2' })));
  const out = h('span', { class: 'muted' });
  return h('div', { class: 'panel' }, h('h3', { text: 'New gallery from a library folder' }),
    h('div', { class: 'row' }, title, folder, vis,
      h('button', { class: 'primary', text: 'Create & scan', onclick: async () => {
        out.textContent = 'Scanning...';
        const r = await api('/api/admin/galleries', { method: 'POST', body: { title: title.value, folder: folder.value, visibility: Number(vis.value) } });
        if (r.ok) route(); else out.textContent = typeof r.data === 'string' ? r.data : 'Failed';
      } }), out));
}

async function viewSettings(app) {
  const me = await api('/api/admin/me');
  if (!me.data || !me.data.admin) return adminLogin(app);
  const r = await api('/api/admin/settings');
  const w = r.data.watermark;
  let hasPng = r.data.hasPng;
  app.append(h('div', { class: 'toolbar' }, h('h1', { text: 'Settings' }), h('span', { class: 'grow' }), h('a', { class: 'btn', href: '#/admin', text: 'Back to admin' })));

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

function galleryPanel(g) {
  const panel = h('div', { class: 'panel' });
  const title = h('input', { type: 'text', value: g.title });
  const slug = h('input', { type: 'text', value: g.slug });
  const desc = h('textarea', { value: g.description || '' });
  const vis = h('select', {}, Object.entries(visLabels).map(([v, t]) => h('option', { value: v, text: t, selected: Number(v) === g.visibility })));
  const pw = h('input', { type: 'password', placeholder: g.hasPassword ? '(password set - type to change, "-" to clear)' : '(no password)', autocomplete: 'new-password' });
  const dl = checkRow('Allow downloads', g.allowDownload), raw = checkRow('Allow RAW download', g.allowRaw), zip = checkRow('Allow ZIP', g.allowZip);
  const up = checkRow('Allow guest uploads (moderated)', g.allowGuestUpload);
  const rawMode = h('select', {}, [[0, 'Same folder, same file name'], [1, 'Same file name, any folder'], [2, 'Name starts with RAW name (e.g. IMG_1234-Edit.png)']].map(([v, t]) => h('option', { value: v, text: t, selected: v === g.rawMatchMode })));
  const wv = checkRow('Watermark on screen', g.watermarkView), wd = checkRow('Watermark downloads (blocks originals & RAW)', g.watermarkDownload);
  const msg = h('span', { class: 'muted' });

  const save = async () => {
    const body = { title: title.value, slug: slug.value, description: desc.value, visibility: Number(vis.value),
      allowDownload: dl.box.checked, allowRaw: raw.box.checked, allowZip: zip.box.checked, allowGuestUpload: up.box.checked,
      watermarkView: wv.box.checked, watermarkDownload: wd.box.checked, rawMatchMode: Number(rawMode.value) };
    if (pw.value) body.password = pw.value === '-' ? '' : pw.value;
    const r = await api('/api/admin/galleries/' + g.id, { method: 'PUT', body });
    if (r.ok) { toast('Saved'); route(); } else msg.textContent = typeof r.data === 'string' ? r.data : 'Failed';
  };

  const linksBox = h('div');
  const loadLinks = async () => {
    const r = await api(`/api/admin/galleries/${g.id}/links`);
    linksBox.replaceChildren(h('table', {},
      h('tr', {}, ['Label', 'Link', 'Expires', 'Downloads', ''].map(t => h('th', { text: t }))),
      (r.data || []).map(l => {
        const url = `${location.origin}/s/${l.token}`;
        const state = l.revoked ? 'revoked' : (l.expiresAt && new Date(l.expiresAt) < new Date() ? 'expired' : '');
        return h('tr', {},
          h('td', { text: l.label || '-' }),
          h('td', {}, h('a', { href: url, text: '/s/' + l.token.slice(0, 8) + '...', target: '_blank' }), ' ',
            h('button', { text: 'Copy', onclick: async () => { try { await navigator.clipboard.writeText(url); toast('Copied'); } catch { prompt('Copy link', url); } } })),
          h('td', { text: (l.expiresAt ? fmtDate(l.expiresAt) : 'never') + (state ? ` (${state})` : '') }),
          h('td', { text: `${l.downloadCount}${l.maxDownloads ? ' / ' + l.maxDownloads : ''}` }),
          h('td', {}, l.revoked ? '' : h('button', { class: 'danger', text: 'Revoke', onclick: async () => { await api('/api/admin/links/' + l.id, { method: 'DELETE' }); loadLinks(); } })));
      })));
  };
  const lLabel = h('input', { type: 'text', placeholder: 'Label (e.g. Team parents)' });
  const lDays = h('input', { type: 'number', placeholder: 'Expires in days', min: 1 });
  const lMax = h('input', { type: 'number', placeholder: 'Max downloads', min: 1 });
  lDays.style.width = '150px'; lMax.style.width = '150px';

  const rawBox = h('div');
  const loadRawLinks = async () => {
    rawBox.replaceChildren();
    const r = await api('/api/g/' + encodeURIComponent(g.slug));
    const photos = (r.data && r.data.photos) || [];
    const rawOnly = photos.filter(p => p.rawOnly);
    const candidates = photos.filter(p => !p.raw && !p.rawOnly);
    if (!rawOnly.length) return;
    const listId = 'dl-' + g.id;
    const dlist = h('datalist', { id: listId }, candidates.map(p => h('option', { value: p.name })));
    rawBox.append(h('h3', { text: `RAW files without an edited version (${rawOnly.length})` }),
      h('p', { class: 'muted', text: 'If a RAW belongs to an edited image whose name does not match, type that image file name to link them.' }), dlist,
      h('table', {}, rawOnly.map(p => {
        const input = h('input', { type: 'text', placeholder: 'Edited image file name', list: listId });
        return h('tr', {}, h('td', {}, h('img', { src: `/img/${p.id}/thumb`, alt: '', class: 'tiny' })), h('td', { text: p.name }),
          h('td', {}, input, ' ', h('button', { text: 'Link', onclick: async () => {
            const target = candidates.find(c => c.name.toLowerCase() === input.value.trim().toLowerCase());
            if (!target) { toast('No matching image name'); return; }
            const lr = await api(`/api/admin/photos/${target.id}/link-raw`, { method: 'POST', body: { rawPhotoId: p.id } });
            if (lr.ok) { toast('Linked'); loadRawLinks(); } else toast(typeof lr.data === 'string' ? lr.data : 'Failed');
          } })));
      })));
  };
  const pendingBox = h('div');
  const loadPending = async () => {
    pendingBox.replaceChildren();
    if (!g.pending) return;
    const r = await api('/api/g/' + encodeURIComponent(g.slug));
    const pend = (r.data && r.data.photos || []).filter(p => !p.approved);
    pendingBox.append(h('h3', { text: `Pending guest uploads (${pend.length})` }),
      h('div', { class: 'grid' }, pend.map(p => {
        const it = h('div', { class: 'ph pending' }, h('img', { src: `/img/${p.id}/thumb`, alt: p.name }));
        it.style.width = '180px'; it.style.height = '140px'; it.style.cursor = 'default';
        const bar = h('div', { class: 'row' },
          h('button', { class: 'primary', text: 'Approve', onclick: async () => { await api(`/api/admin/photos/${p.id}/approve`, { method: 'POST' }); route(); } }),
          h('button', { class: 'danger', text: 'Reject', onclick: async () => { await api(`/api/admin/photos/${p.id}`, { method: 'DELETE' }); route(); } }));
        return h('div', {}, it, bar);
      })));
  };

  panel.append(
    h('div', { class: 'toolbar' }, g.cover ? h('img', { src: `/img/${g.cover}/thumb`, alt: 'cover', class: 'tiny' }) : null, h('h3', { text: g.title }), h('span', { class: 'muted', text: `${g.count} photos · folder: ${g.folder}` }), h('span', { class: 'grow' }),
      h('a', { class: 'btn', href: '#/g/' + encodeURIComponent(g.slug), text: 'Open' }),
      h('button', { text: 'Rescan folder', onclick: async () => {
        msg.textContent = 'Scanning...'; const r = await api(`/api/admin/galleries/${g.id}/scan`, { method: 'POST' });
        msg.textContent = r.ok ? `Added ${r.data.added}, removed ${r.data.removed}; ${r.data.paired} paired with RAW, ${r.data.rawOnly} RAW-only${r.data.skippedRaw ? ', skipped ' + r.data.skippedRaw : ''}` : (r.data || 'Failed'); if (r.ok) loadRawLinks();
      } }),
      h('button', { class: 'danger', text: 'Delete gallery', onclick: async () => {
        if (!confirm('Remove this gallery from the site? Your original photo files are NOT deleted.')) return;
        await api('/api/admin/galleries/' + g.id, { method: 'DELETE' }); route();
      } })),
    h('div', { class: 'form' },
      h('label', { text: 'Title' }), title, h('label', { text: 'URL slug' }), slug, h('label', { text: 'Description' }), desc,
      h('label', { text: 'Visibility' }), vis, h('label', { text: 'Password' }), pw,
      h('label', { text: 'Match RAW by' }), rawMode),
    h('div', { class: 'row' }, dl.el, raw.el, zip.el, up.el),
    h('div', { class: 'row' }, wv.el, wd.el),
    h('div', { class: 'row' }, h('button', { class: 'primary', text: 'Save settings', onclick: save }), msg),
    h('h3', { text: 'Share links' }),
    h('div', { class: 'row' }, lLabel, lDays, lMax,
      h('button', { text: 'Create link', onclick: async () => {
        const r = await api(`/api/admin/galleries/${g.id}/links`, { method: 'POST', body: { label: lLabel.value, expiresInDays: Number(lDays.value) || null, maxDownloads: Number(lMax.value) || null } });
        if (r.ok) { lLabel.value = ''; lDays.value = ''; lMax.value = ''; loadLinks(); }
      } })),
    linksBox, pendingBox, rawBox);
  loadLinks(); loadPending(); loadRawLinks();
  return panel;
}

// ---------- boot ----------
(async function boot() {
  try { const r = await api('/api/site'); if (r.ok) site = r.data; } catch { }
  renderHeader();
  route();
})();
