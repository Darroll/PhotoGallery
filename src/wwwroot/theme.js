// Runs before the page paints: applies the visitor's saved theme, or the site default, so there is no flash of the wrong colors.
(function () {
  var d = document.documentElement, t = d.getAttribute('data-default-theme') || 'auto';
  try {
    if (d.getAttribute('data-theme-choice') === '1') {
      var saved = localStorage.getItem('pg-theme');
      if (saved && /^[a-z]+$/.test(saved)) t = saved;
    }
  } catch (e) { }
  if (t !== 'auto') d.setAttribute('data-theme', t);
})();
