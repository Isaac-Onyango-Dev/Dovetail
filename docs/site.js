/* Dovetail download page.
 *
 * The deploy step stamps the latest release's links and version into the HTML, so the download
 * button works with JavaScript off or GitHub's API rate-limited. This file makes the page
 * live on top of that: download count, patch notes, the version pill, and a few game touches. */

const REPO = 'Isaac-Onyango-Dev/Dovetail';
const API = `https://api.github.com/repos/${REPO}/releases?per_page=100`;
const CACHE_KEY = 'dovetail.releases.v1';
const CACHE_TTL = 10 * 60 * 1000;   // GitHub allows 60 unauthenticated calls an hour per visitor
const REDUCED = matchMedia('(prefers-reduced-motion: reduce)').matches;
const $ = (id) => document.getElementById(id);
const $$ = (sel) => document.querySelectorAll(sel);

/* ── storage (private mode can throw on both read and write) ── */
function cacheRead() {
  try { return JSON.parse(localStorage.getItem(CACHE_KEY)); } catch { return null; }
}
function cacheWrite(data) {
  try { localStorage.setItem(CACHE_KEY, JSON.stringify({ t: Date.now(), data })); } catch { /* fine without it */ }
}

/* ── sticky bar + ember parallax, one rAF loop ── */
function initScroll() {
  const bar = $('topbar');
  const blobs = REDUCED ? [] : [...$$('.blob')];
  let ticking = false;
  const paint = () => {
    const y = scrollY;
    bar.dataset.stuck = y > 24 ? '1' : '0';
    for (const b of blobs) b.style.setProperty('--py', `${(-y * Number(b.dataset.drift || 0)).toFixed(1)}px`);
    ticking = false;
  };
  addEventListener('scroll', () => { if (!ticking) { ticking = true; requestAnimationFrame(paint); } }, { passive: true });
  paint();
}

/* ── mobile menu ── */
function initSheet() {
  const burger = $('burger'), sheet = $('sheet');
  const set = (open) => {
    burger.setAttribute('aria-expanded', String(open));
    burger.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
    sheet.hidden = !open;
    document.body.style.overflow = open ? 'hidden' : '';
  };
  burger.addEventListener('click', () => set(sheet.hidden));
  sheet.addEventListener('click', (e) => { if (e.target.closest('a')) set(false); });
  addEventListener('keydown', (e) => { if (e.key === 'Escape' && !sheet.hidden) set(false); });
  matchMedia('(min-width: 1021px)').addEventListener('change', (m) => { if (m.matches) set(false); });
}

/* ── scroll reveals ── */
function initReveals() {
  const nodes = $$('.reveal');
  if (!('IntersectionObserver' in window)) { nodes.forEach((n) => n.classList.add('in')); return; }
  const io = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      setTimeout(() => e.target.classList.add('in'), Number(e.target.dataset.delay || 0));
      io.unobserve(e.target);
    }
  }, { rootMargin: '0px 0px -8% 0px', threshold: 0.12 });
  nodes.forEach((n) => io.observe(n));
}

/* ── releases ── */
async function loadReleases() {
  const cached = cacheRead();
  if (cached && Date.now() - cached.t < CACHE_TTL) return cached.data;
  try {
    // html+json has GitHub render and sanitise each release body.
    const res = await fetch(API, { headers: { Accept: 'application/vnd.github.html+json' } });
    if (!res.ok) throw new Error(`GitHub ${res.status}`);
    const data = (await res.json()).filter((r) => !r.draft).map((r) => ({
      tag: r.tag_name, url: r.html_url, date: r.published_at, pre: r.prerelease, html: r.body_html || '',
      assets: r.assets.map((a) => ({ name: a.name, url: a.browser_download_url, size: a.size, n: a.download_count, digest: a.digest || '' })),
    }));
    cacheWrite(data);
    return data;
  } catch (err) {
    if (cached) return cached.data;   // stale beats nothing
    throw err;
  }
}

/* ── the score counter ── */
function showScore(total) {
  const wrap = $('score'), num = $('score-num');
  if (!Number.isFinite(total) || total <= 0) { showBadge(); return; }
  const digits = Math.max(5, String(total).length);
  const render = (n) => {
    const s = String(n);
    num.innerHTML = `<span class="pad">${'0'.repeat(digits - s.length)}</span>${s}`;
  };
  wrap.dataset.state = 'ready';
  wrap.setAttribute('aria-label', `${total.toLocaleString()} downloads`);
  if (REDUCED) { render(total); return; }
  const run = () => {
    const t0 = performance.now(), ms = 1500;
    const step = (now) => {
      const p = Math.min((now - t0) / ms, 1);
      render(Math.round(total * (1 - Math.pow(1 - p, 3))));
      if (p < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  };
  const io = new IntersectionObserver((e) => { if (e[0].isIntersecting) { run(); io.disconnect(); } });
  io.observe(wrap);
}
function showBadge() {
  const wrap = $('score'), img = $('score-badge');
  img.onerror = () => { wrap.dataset.state = 'gone'; };   // never a broken image
  img.alt = 'Total downloads';
  img.src = `https://img.shields.io/github/downloads/${REPO}/total?style=flat-square&label=downloads&color=E8483A&labelColor=13131A`;
  wrap.dataset.state = 'badge';
}

/* ── links, version, pill ── */
function applyLatest(latest) {
  const setup = latest.assets.find((a) => /^DovetailSetup-.+\.exe$/i.test(a.name));
  if (!setup) return;
  const zip = latest.assets.find((a) => /portable\.zip$/i.test(a.name));
  const mb = Math.round(setup.size / 1048576);
  $$('[data-setup]').forEach((a) => { a.href = setup.url; });
  if (zip) $$('[data-zip]').forEach((a) => { a.href = zip.url; });
  $$('[data-version], [data-version-tag]').forEach((s) => { s.textContent = latest.tag; });
  $$('[data-summary]').forEach((s) => { s.textContent = `${latest.tag} · ${mb} MB · Windows 10 / 11 · 64-bit`; });

  // The notes open with a bold one-line summary; use it as the pill's headline.
  const lead = new DOMParser().parseFromString(latest.html, 'text/html').querySelector('strong');
  if (lead) $('pill-text').textContent = lead.textContent.trim();

  if (setup.digest.startsWith('sha256:')) {
    $('hash').textContent = setup.digest.slice(7);
    $('hash-cmd').textContent = `Get-FileHash .\\${setup.name}`;
    $('verify').hidden = false;
  }
}

/* ── patch notes: every release, in full ── */
const fmtDate = (s) => new Date(s).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
function renderFeed(list, latest) {
  const feed = $('feed');
  if (!list.length) {
    feed.innerHTML = '<p class="feed-msg">No releases yet.</p>';
  } else {
    feed.replaceChildren(...list.map((r, i) => {
      const d = document.createElement('details');
      d.className = 'patch panel';
      d.open = i === 0;
      d.innerHTML = '<summary><span class="patch-ver"></span><span class="patch-date"></span><i aria-hidden="true"></i><span class="patch-title"></span></summary><div class="notes"></div>';
      d.querySelector('.patch-ver').textContent = r.tag;
      d.querySelector('.patch-date').textContent = fmtDate(r.date);
      if (r === latest) d.querySelector('.patch-date').insertAdjacentHTML('afterend', '<span class="patch-new">Latest patch</span>');
      const notes = d.querySelector('.notes');
      notes.innerHTML = r.html;
      const lead = notes.querySelector('strong');
      d.querySelector('.patch-title').textContent = lead ? lead.textContent.trim() : '';
      if (!r.html) {
        const a = document.createElement('a');
        a.href = r.url; a.textContent = 'Read these notes on GitHub';
        notes.append(a);
      }
      return d;
    }));
  }
  feed.setAttribute('aria-busy', 'false');
}
function feedFailed() {
  const feed = $('feed');
  feed.innerHTML = `<p class="feed-msg">Patch notes couldn't load right now. <a href="https://github.com/${REPO}/releases">Read them on GitHub</a>.</p>`;
  feed.setAttribute('aria-busy', 'false');
}

/* ── screenshot strip: arrows, and a slow drift while nobody's touching it ── */
function initShots() {
  const strip = $('shot-strip');
  let pausedUntil = 0, visible = false;
  const hold = () => { pausedUntil = Date.now() + 9000; };
  const step = () => (strip.querySelector('.shot')?.getBoundingClientRect().width || 400) + 24;
  const go = (dir) => {
    const max = strip.scrollWidth - strip.clientWidth - 4;
    if (dir > 0 && strip.scrollLeft >= max) strip.scrollTo({ left: 0, behavior: 'smooth' });
    else if (dir < 0 && strip.scrollLeft <= 4) strip.scrollTo({ left: max, behavior: 'smooth' });
    else strip.scrollBy({ left: dir * step(), behavior: 'smooth' });
  };
  $('shot-prev').addEventListener('click', () => { go(-1); hold(); });
  $('shot-next').addEventListener('click', () => { go(1); hold(); });
  strip.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowRight') { e.preventDefault(); go(1); hold(); }
    if (e.key === 'ArrowLeft') { e.preventDefault(); go(-1); hold(); }
  });
  if (REDUCED) return;
  ['pointerenter', 'pointerdown', 'focusin', 'wheel', 'touchstart'].forEach((ev) => strip.addEventListener(ev, hold, { passive: true }));
  new IntersectionObserver((e) => { visible = e[0].isIntersecting; }).observe(strip);
  setInterval(() => { if (visible && Date.now() > pausedUntil && !document.hidden) go(1); }, 4200);
}

/* ── toasts ── */
function toast(title, body, ms = 6000) {
  const t = document.createElement('div');
  t.className = 'toast';
  t.innerHTML = '<span class="achv-ico"><svg><use href="#i-trophy"/></svg></span><p><strong></strong><span></span></p>';
  t.querySelector('strong').textContent = title;
  t.querySelector('p span').textContent = body;
  $('toasts').append(t);
  setTimeout(() => { t.classList.add('out'); setTimeout(() => t.remove(), 400); }, ms);
}

/* ── plug a pad in while you're here ── */
function initGamepad() {
  if (!('getGamepads' in navigator)) return;
  const seen = new Set();
  addEventListener('gamepadconnected', (e) => {
    const id = e.gamepad.id || 'a controller';
    if (seen.has(id)) return;
    seen.add(id);
    const xbox = /xinput|xbox/i.test(id);
    const name = id.replace(/\s*\(.*$/, '').slice(0, 48) || 'a controller';
    if (xbox) toast('Xbox-style controller detected', 'Your browser sees an Xbox pad. If that\'s Dovetail at work, you\'re all set.', 8000);
    else toast('Controller detected', `Your browser sees "${name}". A game that only wants Xbox pads may not — that's the fix Dovetail is for.`, 9000);
  });
}

/* ── ↑↑↓↓←→←→BA ── */
function initKonami() {
  const code = ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a'];
  let at = 0;
  addEventListener('keydown', (e) => {
    const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    at = k === code[at] ? at + 1 : (k === code[0] ? 1 : 0);
    if (at < code.length) return;
    at = 0;
    toast('Cheat activated', 'Infinite continues. Your pad still needs calibrating, though.');
    if (REDUCED) return;
    const colors = ['#FFC24D', '#FF7A3D', '#E8483A', '#C21F4B', '#F6F1EE'];
    for (let i = 0; i < 90; i++) {
      const c = document.createElement('i');
      c.className = 'confetti';
      c.style.left = `${Math.random() * 100}vw`;
      c.style.background = colors[i % colors.length];
      c.style.setProperty('--dx', `${(Math.random() - 0.5) * 240}px`);
      c.style.setProperty('--r', `${Math.random() * 900 - 450}deg`);
      c.style.animationDuration = `${1.6 + Math.random() * 1.8}s`;
      c.style.animationDelay = `${Math.random() * 0.4}s`;
      document.body.append(c);
      c.addEventListener('animationend', () => c.remove());
    }
  });
}

/* ── boot ── */
$('year').textContent = new Date().getFullYear();
if (!/Windows/i.test(navigator.userAgent)) $('winonly').hidden = false;
initScroll();
initSheet();
initReveals();
initShots();
initGamepad();
initKonami();

loadReleases().then((list) => {
  const total = list.reduce((sum, r) => sum + r.assets.reduce((s, a) => s + (a.n || 0), 0), 0);
  showScore(total);
  const latest = list.find((r) => !r.pre);
  if (latest) applyLatest(latest);
  renderFeed(list, latest);
}).catch(() => {
  showBadge();
  feedFailed();
});
