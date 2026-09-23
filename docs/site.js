// Theme toggle and the results-chart tooltips. Everything works without this file (each chart has direct
// labels and a table view); it only adds the manual theme switch and hover/focus readouts.
(function () {
  'use strict';

  // ---- theme toggle -------------------------------------------------------------------------------------
  var root = document.documentElement;
  var button = document.getElementById('theme-toggle');
  function effective() {
    var set = root.getAttribute('data-theme');
    if (set) return set;
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  function label() { if (button) button.textContent = effective() === 'dark' ? 'Light mode' : 'Dark mode'; }
  if (window.matchMedia) { var mq = window.matchMedia("(prefers-color-scheme: dark)"); var relabel = function () { label(); }; if (mq.addEventListener) mq.addEventListener("change", relabel); else if (mq.addListener) mq.addListener(relabel); }
  if (button) {
    label();
    button.addEventListener('click', function () {
      var next = effective() === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem('lgp-theme', next); } catch (e) { /* storage can be blocked; the choice just won't persist */ }
      label();
    });
  }

  // ---- chart tooltips (one per chart card) --------------------------------------------------------------
  function wire(card) {
    var tip = card.querySelector('.tip');
    if (!tip) return;
    var rows = card.querySelectorAll('.chart .row');

    function show(row, clientX, clientY) {
      tip.textContent = '';
      var value = document.createElement('span'); value.className = 'tv'; value.textContent = row.getAttribute('data-value');
      var name = document.createElement('span'); name.className = 'tn';
      var key = document.createElement('span'); key.className = 'key'; key.style.background = row.getAttribute('data-color');
      var text = document.createElement('span'); text.textContent = row.getAttribute('data-name');
      name.appendChild(key); name.appendChild(text);
      var desc = document.createElement('span'); desc.className = 'td'; desc.textContent = row.getAttribute('data-desc');
      tip.appendChild(value); tip.appendChild(name); tip.appendChild(desc);
      var box = card.getBoundingClientRect();
      var x, y;
      if (clientX == null) {   // keyboard focus: anchor to the row's bar
        var r = row.getBoundingClientRect(); x = r.left + r.width * 0.5 - box.left; y = r.top - box.top;
      } else { x = clientX - box.left + 14; y = clientY - box.top + 14; }
      tip.classList.add('show');
      var w = tip.offsetWidth, h = tip.offsetHeight;
      x = Math.max(8, Math.min(x, box.width - w - 8));
      y = Math.max(8, Math.min(y, box.height - h - 8));
      tip.style.left = x + 'px'; tip.style.top = y + 'px';
    }
    function hide() { tip.classList.remove('show'); }

    Array.prototype.forEach.call(rows, function (row) {
      row.addEventListener('pointerenter', function (e) { show(row, e.clientX, e.clientY); });
      row.addEventListener('pointermove', function (e) { show(row, e.clientX, e.clientY); });
      row.addEventListener('pointerleave', hide);
      row.addEventListener('focus', function () { show(row, null, null); });
      row.addEventListener('blur', hide);
    });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') hide(); });
  }
  Array.prototype.forEach.call(document.querySelectorAll('.chart-card'), wire);
})();

// Open the FAQ / troubleshooting entry that a link points at (e.g. faq.html#co-op), and keep it in view.
(function () {
  'use strict';
  function openTarget() {
    if (!location.hash) return;
    var el = document.getElementById(decodeURIComponent(location.hash.slice(1)));
    if (!el) return;
    var d = el.tagName === 'DETAILS' ? el : el.closest && el.closest('details');
    if (d) { d.open = true; el.scrollIntoView(); }
  }
  window.addEventListener('hashchange', openTarget);
  window.addEventListener('DOMContentLoaded', openTarget);
  if (document.readyState !== 'loading') openTarget();
})();

// The timing tower on the home page: its twelve rows switch on in order once, like the startup line being written,
// the first time it scrolls into view. Without this file, or with reduced motion, every row simply reads "on".
(function () {
  'use strict';
  var tower = document.querySelector('[data-tower]');
  if (!tower || !('IntersectionObserver' in window)) return;
  if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  var rows = tower.querySelectorAll('li');
  tower.classList.add('is-armed');
  var io = new IntersectionObserver(function (entries) {
    if (!entries.some(function (e) { return e.isIntersecting; })) return;
    io.disconnect();
    Array.prototype.forEach.call(rows, function (row, i) {
      setTimeout(function () { row.classList.add('is-live'); }, 120 + i * 90);
    });
  }, { threshold: 0.35 });
  io.observe(tower);
})();
