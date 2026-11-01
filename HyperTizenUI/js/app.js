(function () {
  'use strict';

  var SERVICE_PORT = 8086;
  var EV = { SetConfig: 0, ReadConfig: 1, ReadConfigResult: 2, ScanSSDP: 3, SSDPScanResult: 4 };

  var S = {
    tvIp: null,
    ws: null,
    svcOk: false,
    attempts: 0,
    rpc: null,            // saved rpcServer (null = not set)
    rpcKnown: false,      // service answered ReadConfig rpcServer
    enabled: false,
    devices: [],
    scanned: false,
    link: 'idle',         // idle | wait | ok | bad
    linkDetail: '',
    hws: null,            // websocket to Hyperion (diagnostics only)
    hTimer: null,
    hRetry: 0,
    image: 'idle',        // idle | wait | ok | bad
    imageDetail: '',
    pending: {},          // key -> expected value (for confirmations)
    rpcTimer: null,
    saveFailed: false,    // service ignored the last server choice
    logs: [],
    logMore: false,
    t0: Date.now()
  };

  function $(id) { return document.getElementById(id); }
  function two(n) { return n < 10 ? '0' + n : '' + n; }

  function log(msg) {
    var d = new Date();
    S.logs.push(two(d.getHours()) + ':' + two(d.getMinutes()) + ':' + two(d.getSeconds()) + '  ' + msg);
    if (S.logs.length > 200) S.logs.shift();
    renderLog();
  }
  function renderLog() {
    var n = S.logMore ? 24 : 4;
    $('log').textContent = S.logs.slice(-n).join('\n');
    $('btnLog').textContent = S.logMore ? 'Arată mai puțin' : 'Arată mai mult';
  }

  var toastTimer = null;
  function toast(msg) {
    var t = $('toast');
    t.textContent = msg;
    t.style.display = 'block';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.style.display = 'none'; }, 3500);
  }

  /* ---------- 1. Service on the TV (ws :8086) ---------- */

  function getTvIp() {
    log('Caut IP-ul TV-ului (TizenBrew, 127.0.0.1:8081)…');
    fetch('http://127.0.0.1:8081')
      .then(function (r) { return r.text(); })
      .then(function (ip) {
        S.tvIp = ip.trim();
        log('IP TV: ' + S.tvIp);
        connectService();
      })
      .catch(function (e) {
        log('TizenBrew nu a răspuns: ' + (e && e.message ? e.message : e));
        S.attempts++;
        render();
        setTimeout(getTvIp, 2500);
      });
  }

  function connectService() {
    S.attempts++;
    log('Mă conectez la serviciu (ws://' + S.tvIp + ':' + SERVICE_PORT + '), încercarea ' + S.attempts);
    var ws;
    try { ws = new WebSocket('ws://' + S.tvIp + ':' + SERVICE_PORT); }
    catch (e) { log('Adresă invalidă: ' + e.message); setTimeout(connectService, 2500); return; }
    S.ws = ws;
    var opened = false;
    ws.onopen = function () {
      opened = true;
      S.svcOk = true;
      S.attempts = 0;
      log('Serviciul HyperTizen răspunde.');
      sendRead('rpcServer');
      sendRead('enabled');
      scan();
      render();
    };
    ws.onmessage = onServiceMessage;
    ws.onerror = function () { /* handled in onclose */ };
    ws.onclose = function () {
      var was = S.svcOk;
      S.svcOk = false;
      S.ws = null;
      if (was) log('Legătura cu serviciul s-a pierdut.');
      else if (!opened) log('Serviciul nu răspunde pe portul ' + SERVICE_PORT + '.');
      render();
      setTimeout(connectService, was ? 1000 : 2500);
    };
  }

  function sendSvc(obj) {
    if (S.ws && S.ws.readyState === 1) { S.ws.send(JSON.stringify(obj)); return true; }
    return false;
  }
  function sendRead(key) { sendSvc({ event: EV.ReadConfig, key: key }); }
  function scan() { sendSvc({ event: EV.ScanSSDP }); }

  function onServiceMessage(e) {
    var m;
    try { m = JSON.parse(e.data); } catch (x) { log('Mesaj ilizibil de la serviciu.'); return; }
    var ev = m.Event !== undefined ? m.Event : m.event;
    if (ev === EV.ReadConfigResult) {
      if (m.error) { log('Setarea „' + m.key + '” nu a putut fi citită: ' + m.error); return; }
      var val = (m.value === undefined || m.value === '') ? null : m.value;
      if (m.key === 'rpcServer') {
        S.rpcKnown = true;
        var changed = S.rpc !== val;
        S.rpc = val;
        log('Server salvat: ' + (val === null ? 'niciunul' : val));
        if (S.pending.rpcServer !== undefined) {
          clearTimeout(S.rpcTimer);
          if (val === S.pending.rpcServer) { S.saveFailed = false; toast('Server salvat'); }
          else { S.saveFailed = true; toast('Serviciul nu a păstrat setarea'); log('Serviciul a răspuns, dar nu a păstrat serverul ales.'); }
          delete S.pending.rpcServer;
        }
        if (changed) { S.link = val ? 'wait' : 'idle'; S.linkDetail = ''; connectHyperion(); }
      } else if (m.key === 'enabled') {
        S.enabled = String(val) === 'true';
        log('Captură: ' + (S.enabled ? 'pornită' : 'oprită'));
        if (S.pending.enabled !== undefined) {
          if (String(S.enabled) === S.pending.enabled) toast(S.enabled ? 'Captura a pornit' : 'Captura s-a oprit');
          else toast('Serviciul nu a confirmat schimbarea');
          delete S.pending.enabled;
        }
        S.image = S.enabled ? 'wait' : 'idle';
        S.imageDetail = '';
      }
    } else if (ev === EV.SSDPScanResult) {
      var list = [];
      var arr = m.devices || [];
      for (var i = 0; i < arr.length; i++) {
        var d = arr[i];
        if (!d || !d.UrlBase) continue;
        var u = String(d.UrlBase);
        u = u.indexOf('https') === 0 ? u.replace('https', 'wss') : u.replace('http', 'ws');
        list.push({ url: u, name: d.FriendlyName || u });
      }
      S.devices = list;
      S.scanned = true;
      log('Scanare: ' + list.length + (list.length === 1 ? ' server găsit' : ' servere găsite'));
    } else {
      log('Mesaj necunoscut de la serviciu: ' + e.data.slice(0, 80));
    }
    render();
  }

  function chooseDevice(url) {
    S.pending.rpcServer = url;
    if (!sendSvc({ event: EV.SetConfig, key: 'rpcServer', value: url })) { toast('Serviciul nu e conectat'); return; }
    log('Aleg serverul ' + url);
    sendRead('rpcServer');
    clearTimeout(S.rpcTimer);
    S.rpcTimer = setTimeout(function () {
      if (S.pending.rpcServer === undefined) return;
      delete S.pending.rpcServer;
      log('Serviciul nu a răspuns la alegerea serverului (3 secunde).');
      toast('Serviciul nu a confirmat alegerea');
      S.saveFailed = true;
      render();
    }, 3000);
  }

  function toggleCapture() {
    if (!S.rpc) { toast('Alege mai întâi un server Hyperion'); return; }
    var next = !S.enabled;
    S.pending.enabled = String(next);
    if (!sendSvc({ event: EV.SetConfig, key: 'enabled', value: String(next) })) { toast('Serviciul nu e conectat'); return; }
    log(next ? 'Cer pornirea capturii…' : 'Cer oprirea capturii…');
    sendRead('enabled');
  }

  /* ---------- 2/3. Diagnostics against Hyperion's JSON API ---------- */

  function closeHyperion() {
    clearInterval(S.hTimer);
    S.hTimer = null;
    if (S.hws) { S.hws.onclose = null; try { S.hws.close(); } catch (e) {} S.hws = null; }
  }

  function connectHyperion() {
    closeHyperion();
    if (!S.rpc) { render(); return; }
    var url = S.rpc;
    log('Verific legătura TV → server: ' + url);
    var ws;
    try { ws = new WebSocket(url); }
    catch (e) { S.link = 'bad'; S.linkDetail = 'Adresă invalidă.'; render(); return; }
    S.hws = ws;
    var got = false;
    ws.onopen = function () {
      ws.send(JSON.stringify({ command: 'serverinfo', tan: 1 }));
      S.hTimer = setInterval(function () {
        if (ws.readyState === 1) ws.send(JSON.stringify({ command: 'serverinfo', tan: 1 }));
      }, 3000);
    };
    ws.onmessage = function (e) {
      var m;
      try { m = JSON.parse(e.data); } catch (x) { return; }
      if (m.command !== 'serverinfo' || !m.info) return;
      if (!got) { got = true; log('Serverul Hyperion răspunde.'); }
      S.hRetry = 0;
      S.link = 'ok';
      S.linkDetail = '';
      readPriorities(m.info.priorities || []);
      render();
    };
    ws.onerror = function () { /* handled in onclose */ };
    ws.onclose = function () {
      clearInterval(S.hTimer);
      S.hws = null;
      S.link = 'bad';
      S.linkDetail = got
        ? 'Legătura s-a pierdut.'
        : 'TV-ul nu poate deschide ' + url + '. Cauza obișnuită: firewall-ul Windows blochează portul sau serverul nu rulează.';
      if (!got) log('Nu pot ajunge la ' + url);
      render();
      S.hRetry++;
      setTimeout(function () { if (S.rpc === url && !S.hws) connectHyperion(); }, Math.min(3000 * S.hRetry, 12000));
    };
    render();
  }

  function readPriorities(list) {
    var mine = null;
    for (var i = 0; i < list.length; i++) {
      var txt = JSON.stringify(list[i]).toLowerCase();
      if (txt.indexOf('hypertizen') !== -1) { mine = list[i]; break; }
    }
    if (!S.enabled) { S.image = 'idle'; S.imageDetail = ''; return; }
    if (mine) {
      if (S.image !== 'ok') log('Hyperion primește imaginea de la HyperTizen.');
      S.image = 'ok';
      S.imageDetail = 'Prioritate ' + mine.priority + (mine.active ? ', activă' : ', inactivă');
    } else {
      S.image = 'wait';
      S.imageDetail = 'Captura e pornită, dar Hyperion nu vede încă nicio sursă de la TV.';
    }
  }

  /* ---------- Render ---------- */

  function setNode(i, state, status, detail) {
    var n = $('n' + i);
    n.setAttribute('data-s', state);
    n.querySelector('.st').textContent = status;
    n.querySelector('.dt').textContent = detail || '';
  }

  function render() {
    // chain
    if (S.svcOk) setNode(0, 'ok', 'Rulează', S.tvIp);
    else setNode(0, S.attempts > 2 ? 'bad' : 'wait', S.attempts > 2 ? 'Nu răspunde' : 'Se conectează…',
      S.tvIp ? 'încercarea ' + S.attempts : 'caut TizenBrew…');

    if (!S.svcOk) setNode(1, 'idle', '—', '');
    else if (S.rpc) setNode(1, 'ok', 'Ales', S.rpc);
    else setNode(1, 'wait', 'Neales', S.scanned ? (S.devices.length ? 'alege unul din listă' : 'niciun server găsit în rețea') : 'scanez rețeaua…');

    if (!S.rpc) setNode(2, 'idle', '—', '');
    else if (S.link === 'ok') setNode(2, 'ok', 'Merge', 'TV-ul vede serverul');
    else if (S.link === 'bad') setNode(2, 'bad', 'Blocată', 'TV-ul nu deschide serverul');
    else setNode(2, 'wait', 'Se verifică…', '');

    if (!S.enabled) setNode(3, 'idle', '—', 'captura e oprită');
    else if (S.image === 'ok') setNode(3, 'ok', 'Da', S.imageDetail);
    else setNode(3, 'wait', 'Încă nu', S.imageDetail);

    // verdict
    var lvl, title, detail;
    if (!S.svcOk) {
      lvl = S.attempts > 2 ? 'bad' : 'wait';
      title = 'Serviciul HyperTizen nu răspunde';
      detail = S.tvIp
        ? 'Interfața încearcă din nou la câteva secunde. Dacă rămâne așa, serviciul nu rulează pe TV: reinstalează pachetul sau repornește TV-ul.'
        : 'Aștept răspunsul TizenBrew.';
    } else if (!S.rpc && S.saveFailed) {
      lvl = 'bad';
      title = 'Serviciul nu salvează serverul ales';
      detail = 'Serviciul de pe TV primește alegerea, dar nu răspunde și nu o păstrează. Asta indică o eroare în serviciu, nu în rețea sau în interfață.';
    } else if (!S.rpc) {
      lvl = 'wait';
      title = 'Alege serverul Hyperion';
      detail = S.devices.length
        ? 'Selectează serverul din listă. Interfața confirmă salvarea.'
        : 'Nu am găsit niciun server. Pornește Hyperion sau HyperHDR pe PC, în aceeași rețea.';
    } else if (S.link === 'bad') {
      lvl = 'bad';
      title = 'TV-ul nu ajunge la serverul Hyperion';
      detail = S.linkDetail;
    } else if (S.link !== 'ok') {
      lvl = 'wait'; title = 'Verific legătura cu serverul…'; detail = '';
    } else if (!S.enabled) {
      lvl = 'wait'; title = 'Totul e pregătit'; detail = 'Apasă „Pornește captura”.';
    } else if (S.image !== 'ok') {
      lvl = 'wait';
      title = 'Captura e pornită, dar nu ajunge imagine';
      detail = 'Serviciul de pe TV nu trimite imagini către Hyperion. Cauza probabilă: TV-ul nu oferă funcțiile de captură de care are nevoie HyperTizen. Încearcă și cu alt conținut (de exemplu o intrare HDMI).';
    } else {
      lvl = 'ok'; title = 'Funcționează'; detail = 'Hyperion primește imaginea de pe TV.';
    }
    document.body.setAttribute('data-level', lvl);
    $('vTitle').textContent = title;
    $('vDetail').textContent = detail;

    // meta
    $('meta').textContent = S.tvIp ? 'TV ' + S.tvIp : 'Pornire…';

    // server panel
    $('rpcLine').textContent = S.rpc ? 'Server folosit: ' + S.rpc : 'Niciun server ales.';
    renderDevices();

    // capture panel
    $('capLine').textContent = S.enabled ? 'Captura este pornită.' : 'Captura este oprită.';
    $('btnCap').textContent = S.enabled ? 'Oprește captura' : 'Pornește captura';
  }

  function renderDevices() {
    var box = $('devices');
    var focusedUrl = document.activeElement && document.activeElement.getAttribute
      ? document.activeElement.getAttribute('data-url') : null;
    box.textContent = '';
    var shown = S.devices.slice();
    if (S.rpc && !shown.some(function (d) { return d.url === S.rpc; })) {
      shown.unshift({ url: S.rpc, name: 'Server salvat', stale: true });
    }
    if (!shown.length) {
      var p = document.createElement('p');
      p.className = 'empty';
      p.textContent = S.scanned ? 'Niciun server găsit. Caut din nou la 10 secunde.' : 'Scanez rețeaua…';
      box.appendChild(p);
      return;
    }
    shown.forEach(function (d) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'dev' + (d.url === S.rpc ? ' selected' : '') + (d.stale ? ' stale' : '');
      b.setAttribute('data-url', d.url);
      var left = document.createElement('span');
      var nm = document.createElement('span'); nm.className = 'nm'; nm.textContent = d.name;
      var ur = document.createElement('span'); ur.className = 'ur'; ur.textContent = d.url;
      left.appendChild(nm); left.appendChild(ur);
      var tg = document.createElement('span'); tg.className = 'tg';
      tg.textContent = d.url === S.rpc ? 'Ales' : (d.stale ? 'nu răspunde la scanare' : '');
      b.appendChild(left); b.appendChild(tg);
      b.addEventListener('click', function () { chooseDevice(d.url); });
      box.appendChild(b);
      if (focusedUrl && focusedUrl === d.url) b.focus();
    });
  }

  /* ---------- Remote control navigation ---------- */

  function focusables() {
    return Array.prototype.slice.call(document.querySelectorAll('button')).filter(function (b) { return b.offsetParent !== null; });
  }
  function center(el) { var r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }

  function move(dir) {
    var list = focusables();
    if (!list.length) return;
    var cur = document.activeElement;
    if (list.indexOf(cur) === -1) { list[0].focus(); return; }
    var c = center(cur), best = null, bestScore = Infinity;
    list.forEach(function (el) {
      if (el === cur) return;
      var p = center(el), dx = p.x - c.x, dy = p.y - c.y;
      var ok = (dir === 'up' && dy < -4) || (dir === 'down' && dy > 4) || (dir === 'left' && dx < -4) || (dir === 'right' && dx > 4);
      if (!ok) return;
      var main = (dir === 'up' || dir === 'down') ? Math.abs(dy) : Math.abs(dx);
      var side = (dir === 'up' || dir === 'down') ? Math.abs(dx) : Math.abs(dy);
      var score = main + side * 2;
      if (score < bestScore) { bestScore = score; best = el; }
    });
    if (best) { best.focus(); best.scrollIntoView({ block: 'nearest' }); }
  }

  document.addEventListener('keydown', function (e) {
    var k = e.key, code = e.keyCode;
    if (k === 'ArrowDown') { e.preventDefault(); move('down'); }
    else if (k === 'ArrowUp') { e.preventDefault(); move('up'); }
    else if (k === 'ArrowLeft') { e.preventDefault(); move('left'); }
    else if (k === 'ArrowRight') { e.preventDefault(); move('right'); }
    else if (code === 10009 || k === 'Escape') {
      try { tizen.application.getCurrentApplication().exit(); } catch (x) {}
    }
  });

  /* ---------- Start ---------- */

  window.addEventListener('load', function () {
    $('btnScan').addEventListener('click', function () {
      if (sendSvc({ event: EV.ScanSSDP })) { log('Caut servere…'); toast('Caut servere…'); } else toast('Serviciul nu e conectat');
    });
    $('btnTest').addEventListener('click', function () {
      if (!S.rpc) { toast('Alege mai întâi un server'); return; }
      S.hRetry = 0; S.link = 'wait'; render(); connectHyperion(); toast('Verific legătura…');
    });
    $('btnCap').addEventListener('click', toggleCapture);
    $('btnReload').addEventListener('click', function () { location.reload(); });
    $('btnLog').addEventListener('click', function () { S.logMore = !S.logMore; renderLog(); });
    setInterval(function () { if (S.svcOk) scan(); }, 10000);
    setInterval(function () { if (S.svcOk) sendRead('enabled'); }, 6000);
    log('Interfața a pornit.');
    render();
    var first = $('btnCap'); if (first) first.focus();
    getTvIp();
  });

})();
