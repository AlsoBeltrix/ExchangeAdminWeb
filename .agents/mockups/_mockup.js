// Mockup behaviour only: keeps the "N selected" bars honest so the layouts can be judged
// with something actually ticked. No app code here.
function mkSync(group) {
  var boxes = Array.prototype.slice.call(document.querySelectorAll('input[data-group="' + group + '"]'));
  var picked = boxes.filter(function (b) { return b.checked; });
  document.querySelectorAll('[data-count="' + group + '"]').forEach(function (el) {
    el.textContent = picked.length === 0 ? 'Nothing selected' : picked.length + ' selected';
  });
  document.querySelectorAll('[data-chips="' + group + '"]').forEach(function (el) {
    el.innerHTML = '';
    picked.forEach(function (b) {
      var s = document.createElement('span');
      s.className = 'badge text-bg-light border';
      s.textContent = b.getAttribute('data-label');
      el.appendChild(s);
    });
  });
  document.querySelectorAll('[data-needs="' + group + '"]').forEach(function (el) {
    el.disabled = picked.length === 0;
  });
}
document.addEventListener('change', function (e) {
  var g = e.target && e.target.getAttribute && e.target.getAttribute('data-group');
  if (g) { mkSync(g); }
});
document.addEventListener('DOMContentLoaded', function () {
  var groups = {};
  document.querySelectorAll('input[data-group]').forEach(function (b) { groups[b.getAttribute('data-group')] = 1; });
  Object.keys(groups).forEach(mkSync);
});
function mkDrawer(open) {
  var d = document.getElementById('mkDrawer');
  if (d) { d.classList.toggle('closed', !open); }
}
