using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CustomLLMAPI
{
    public static class PuppetMasterUI
    {
        public static string Build(int port)
        {
            return @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>PuppetMaster</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { background: #0d0d1a; color: #e0e0e0; font-family: 'Segoe UI', system-ui, sans-serif; min-height: 100vh; }
header { background: #16213e; padding: 14px 20px; display: flex; align-items: center; gap: 10px; border-bottom: 1px solid #2d2d5e; }
header h1 { font-size: 1.2em; color: #a78bfa; }
header h1 span { color: #f472b6; }
#dot { width: 10px; height: 10px; border-radius: 50%; background: #4ade80; margin-left: auto; transition: background 0.3s; }
#dot.off { background: #f87171; }
main { padding: 20px; max-width: 600px; margin: 0 auto; display: flex; flex-direction: column; gap: 18px; }
.card { background: #16213e; border-radius: 12px; padding: 18px; border: 1px solid #2d2d5e; }
.card h2 { font-size: 0.85em; text-transform: uppercase; letter-spacing: 0.08em; color: #6b7280; margin-bottom: 14px; }
.btn-grid { display: grid; gap: 10px; }
.btn-grid.cols-2 { grid-template-columns: 1fr 1fr; }
.btn-grid.cols-3 { grid-template-columns: 1fr 1fr 1fr; }
button { border: none; border-radius: 8px; padding: 12px 10px; cursor: pointer; font-size: 0.9em; font-weight: 600; transition: opacity 0.15s, transform 0.1s; }
button:hover { opacity: 0.85; transform: translateY(-1px); }
button:active { transform: translateY(0); }
button.primary { background: #6366f1; color: #fff; }
button.danger  { background: #ef4444; color: #fff; }
button.success { background: #10b981; color: #fff; }
button.warning { background: #f59e0b; color: #000; }
button.neutral { background: #374151; color: #e0e0e0; }
button.purple  { background: #7c3aed; color: #fff; }
button.pink    { background: #ec4899; color: #fff; }
.msg-row { display: flex; gap: 8px; }
#msgText { flex: 1; background: #0d0d1a; border: 1px solid #2d2d5e; border-radius: 8px; padding: 10px 12px; color: #e0e0e0; font-size: 0.9em; outline: none; }
#msgText:focus { border-color: #6366f1; }
#status-bar { font-size: 0.8em; color: #6b7280; text-align: center; padding: 8px; }
.tag { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 0.75em; font-weight: 700; margin-left: 6px; }
.tag.on  { background: #10b981; color: #000; }
.tag.off { background: #374151; color: #9ca3af; }
.slider-row { display: flex; align-items: center; gap: 12px; }
.slider-row input[type=range] { flex: 1; accent-color: #a78bfa; height: 6px; cursor: pointer; }
.slider-row .slider-val { font-size: 0.9em; font-weight: 700; color: #a78bfa; min-width: 38px; text-align: right; }
.slider-row button { flex: 0 0 auto; padding: 8px 12px; font-size: 0.8em; }
</style>
</head>
<body>
<header>
  <h1>Puppet<span>Master</span></h1>
  <span id='avatarName' style='font-size:0.75em;color:#6b7280;margin-left:4px;'>...</span>
  <div id='dot'></div>
</header>
<main>

<div class='card'>
  <h2>Mood</h2>
  <div id='moodBtns' class='btn-grid cols-3'>
    <button class='neutral' disabled>Loading...</button>
  </div>
  <div style='margin-top:10px;display:flex;gap:8px;align-items:center;'>
    <button class='neutral' onclick='reloadMoods()' style='flex:0 0 auto;'>&#8635; Reload Profile</button>
    <span id='moodStatus' style='font-size:0.8em;color:#6b7280;'></span>
  </div>
</div>

  <div class='card'>
    <h2>Dance</h2>
    <div class='btn-grid cols-2'>
      <button class='purple' onclick='startDance()'>&#128131; Start Dancing</button>
      <button class='danger' onclick='stopDance()'>&#9209; Stop Dancing</button>
    </div>
  </div>

    <div class='card'>
      <h2>Walk <span class='tag off' id='walkTag'>OFF</span></h2>
      <div class='btn-grid cols-2'>
        <button class='success' onclick='startWalk()'>&#128694; Start Walking</button>
        <button class='neutral' onclick='stopWalk()'>&#9632; Stop Walking</button>
      </div>
    </div>

<div class='card'>
  <h2>Animations</h2>
  <div class='msg-row'>
    <select id='animSelect' style='flex:1;background:#0d0d1a;border:1px solid #2d2d5e;border-radius:8px;padding:10px 12px;color:#e0e0e0;font-size:0.9em;outline:none;'>
      <option value=''>Loading...</option>
    </select>
    <button class='primary' onclick='triggerAnim(true)'>&#9654; On</button>
    <button class='neutral' onclick='triggerAnim(false)'>&#9632; Off</button>
  </div>
</div>

  <div class='card'>
    <h2>Message</h2>
    <div class='msg-row'>
      <input id='msgText' type='text' placeholder='Say something...' maxlength='120'
             onkeydown=""if(event.key==='Enter') sendMsg()"">
      <button class='pink' onclick='sendMsg()'>&#128172; Send</button>
    </div>
  </div>

  <div class='card'>
    <h2>Big Screen <span class='tag off' id='bsTag'>OFF</span></h2>
    <div class='btn-grid cols-2'>
      <button class='success' onclick='setBigScreen(true)'>&#128250; Enable</button>
      <button class='neutral' onclick='setBigScreen(false)'>&#128444; Disable</button>
    </div>
  </div>

  <div class='card'>
    <h2>Avatar Size</h2>
    <div class='slider-row'>
      <input id='sizeSlider' type='range' min='0.1' max='1.3' step='0.05' value='1'
             oninput=""document.getElementById('sizeVal').textContent=parseFloat(this.value).toFixed(2)+'x'""
             onchange=""setAvatarSize(parseFloat(this.value))"">
      <span class='slider-val' id='sizeVal'>1.00x</span>
      <button class='neutral' onclick=""resetSize()"">Reset</button>
    </div>
  </div>

</main>
<div id='status-bar'>Connecting...</div>

<script>
var PORT = " + port + @";
var base = 'http://localhost:' + PORT;
var statusBar = document.getElementById('status-bar');
var dot = document.getElementById('dot');
var currentMood = '';

function api(method, path, body) {
  var opts = { method: method, headers: { 'Content-Type': 'application/json' } };
  if (body !== null && body !== undefined) opts.body = JSON.stringify(body);
  return fetch(base + path, opts).then(function(r) { return r.json(); });
}

function loadMoods() {
  api('GET', '/mood/list', null)
    .then(function(list) {
      var grid = document.getElementById('moodBtns');
      grid.innerHTML = '';
      list.forEach(function(name) {
        var btn = document.createElement('button');
        btn.id = 'moodBtn_' + name;
        btn.textContent = name;
        btn.className = getMoodClass(name);
        btn.onclick = function() { setMood(name); };
        grid.appendChild(btn);
      });
    })
    .catch(setDisconnected);
}

function getMoodClass(name) {
  var map = {
    'Joy':     'success',
    'Angry':   'danger',
    'Sorrow':  'primary',
    'Fun':     'warning',
    'Neutral': 'neutral'
  };
  return map[name] || 'neutral';
}

function setMood(mood) {
  api('POST', '/mood', { mood: mood })
    .then(function(d) {
      flash(d.status === 'ok' ? '#4ade80' : '#f87171');
      currentMood = mood;
      updateMoodButtons(mood);
      document.getElementById('moodStatus').textContent = 'Active: ' + mood;
    })
    .catch(setDisconnected);
}

function updateMoodButtons(activeMood) {
  var grid = document.getElementById('moodBtns');
  for (var i = 0; i < grid.children.length; i++) {
    var btn = grid.children[i];
    btn.style.outline = btn.textContent === activeMood ? '2px solid #fff' : '';
    btn.style.opacity = btn.textContent === activeMood ? '1' : '0.6';
  }
}

function reloadMoods() {
  api('POST', '/mood/reload', null)
    .then(function(d) {
      document.getElementById('moodStatus').textContent = 'Reloaded — ' + d.moods + ' moods';
      flash('#4ade80');
      loadMoods();
    })
    .catch(setDisconnected);
}

function startDance() {
  api('POST', '/dance/start', { index: 0 })
    .then(function(d) { flash(d.status === 'ok' ? '#4ade80' : '#f87171'); refreshStatus(); })
    .catch(setDisconnected);
}

function stopDance() {
  api('POST', '/dance/stop', null)
    .then(function(d) { flash(d.status === 'ok' ? '#4ade80' : '#f87171'); refreshStatus(); })
    .catch(setDisconnected);
}

function startWalk() {
  api('POST', '/walk/start', null)
    .then(function(d) { flash(d.status === 'ok' ? '#4ade80' : '#f87171'); refreshStatus(); })
    .catch(setDisconnected);
}

function stopWalk() {
  api('POST', '/walk/stop', null)
    .then(function(d) { flash(d.status === 'ok' ? '#4ade80' : '#f87171'); refreshStatus(); })
    .catch(setDisconnected);
}

function loadAnimations() {
  api('GET', '/animations', null)
    .then(function(list) {
      var sel = document.getElementById('animSelect');
      sel.innerHTML = '';
      list.forEach(function(a) {
        var opt = document.createElement('option');
        opt.value = a.name;
        opt.textContent = a.name + ' (' + a.type + ')';
        sel.appendChild(opt);
      });
    })
    .catch(setDisconnected);
}

function triggerAnim(value) {
  var param = document.getElementById('animSelect').value;
  if (!param) return;
  api('POST', '/animation/trigger', { param: param, value: value ? 'true' : 'false' })
    .then(function(d) { flash(d.status === 'ok' ? '#4ade80' : '#f87171'); })
    .catch(setDisconnected);
}

function updateWalkTag(on) {
  var tag = document.getElementById('walkTag');
  tag.textContent = on ? 'ON' : 'OFF';
  tag.className = 'tag ' + (on ? 'on' : 'off');
}

function sendMsg() {
  var text = document.getElementById('msgText').value.trim();
  if (!text) return;
  api('POST', '/message', { text: text })
    .then(function() { document.getElementById('msgText').value = ''; flash('#ec4899'); })
    .catch(setDisconnected);
}

function setBigScreen(active) {
  api('POST', '/bigscreen', { active: active })
    .then(function(d) { updateBsTag(d.bigscreen); flash('#4ade80'); })
    .catch(setDisconnected);
}

function updateBsTag(on) {
  var tag = document.getElementById('bsTag');
  tag.textContent = on ? 'ON' : 'OFF';
  tag.className = 'tag ' + (on ? 'on' : 'off');
}

function setAvatarSize(size) {
  api('POST', '/size', { size: size })
    .then(function(d) { flash(d.status === 'ok' ? '#a78bfa' : '#f87171'); })
    .catch(setDisconnected);
}

function resetSize() {
  var slider = document.getElementById('sizeSlider');
  slider.value = 1;
  document.getElementById('sizeVal').textContent = '1.00x';
  setAvatarSize(1.0);
}

var _sliderDragging = false;
document.addEventListener('DOMContentLoaded', function() {
  var sl = document.getElementById('sizeSlider');
  if (sl) {
    sl.addEventListener('mousedown', function() { _sliderDragging = true; });
    sl.addEventListener('touchstart', function() { _sliderDragging = true; });
    sl.addEventListener('mouseup', function() { _sliderDragging = false; });
    sl.addEventListener('touchend', function() { _sliderDragging = false; });
  }
});

function syncSizeSlider(size) {
  if (_sliderDragging) return;
  var slider = document.getElementById('sizeSlider');
  var label  = document.getElementById('sizeVal');
  if (!slider || !label) return;
  var rounded = Math.round(size * 100) / 100;
  if (Math.abs(parseFloat(slider.value) - rounded) > 0.01) {
    slider.value = rounded;
    label.textContent = rounded.toFixed(2) + 'x';
  }
}

function refreshStatus() {
  api('GET', '/status', null)
    .then(function(s) {
      statusBar.textContent = 'Mood: ' + s.mood + '  |  Dancing: ' + s.dancing + '  |  Walking: ' + s.walking + '  |  BigScreen: ' + s.bigscreen + '  |  Size: ' + (s.size ? parseFloat(s.size).toFixed(2) + 'x' : '—');
      document.getElementById('avatarName').textContent = s.avatar || '';
      updateBsTag(s.bigscreen);
      updateWalkTag(s.walking);
      if (s.size !== undefined) syncSizeSlider(s.size);
      if (s.mood !== currentMood) {
        currentMood = s.mood;
        updateMoodButtons(s.mood);
        document.getElementById('moodStatus').textContent = 'Active: ' + s.mood;
      }
      dot.className = '';
    })
    .catch(setDisconnected);
}

function setDisconnected() {
  statusBar.textContent = 'Disconnected — is the game running?';
  dot.className = 'off';
}

function flash(color) {
  dot.style.background = color;
  setTimeout(function() { dot.style.background = ''; }, 400);
}

setTimeout(function() { loadMoods(); }, 2000);
loadAnimations();
refreshStatus();
setInterval(refreshStatus, 3000);
</script>
</body>
</html>";
        }
    }
}