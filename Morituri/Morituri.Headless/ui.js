// MORITURI UI 게이트웨이 (R1) — 아이콘 단일 관문
// 서버가 발행하는 {토큰}과 구세이브·잔존 텍스트의 레거시 이모지를 icons.svg 스프라이트로 치환한다.
// 규약: 새 코드는 {토큰}만 쓴다(이모지 금지 — designlint가 잡는다). 이모지 맵은 구세이브 호환용.
// 동작: 문서 전체 텍스트 노드를 MutationObserver로 감시 — 렌더 함수들이 개별 처리할 필요 없음.
(function(){
  'use strict';

  // {토큰} → icons.svg 심볼명 (토큰명 = 심볼명)
  var ICONS = ['sword','swords','shield','helmet','fist','bolt','coin','glory','laurel','crown',
    'trophy','star','arena','ludus','calendar','scroll','book','quill','thumb','skull','dice',
    'flame','snow','hourglass','medic','heart','person','recruit','eye','gear','play','ff','pause',
    'masks','fest','coffin','speech','warn','blood','letter','tag','news','archive','horn','candle',
    'dove','handshake','mind','impact','chart','sprout','target','scales','lock','search','help',
    'gem','mug','wine','thumbdown'];
  var ICON = {}; ICONS.forEach(function(n){ ICON[n] = true; });

  // 레거시 이모지 → 심볼명 (구세이브 world.json·아직 청소되지 않은 텍스트 호환)
  var EMO = {
    '💰':'coin','💸':'coin','🪙':'coin','🏛':'ludus','🔥':'flame','📜':'scroll','📋':'scroll','🗒':'scroll','🗺':'scroll',
    '🎭':'masks','⚔':'swords','🤺':'swords','🗡':'sword','🔪':'sword','✨':'glory','👑':'crown','🎲':'dice',
    '🏆':'trophy','⚰':'coffin','⚱':'coffin','🎪':'fest','🎖':'laurel','🏅':'laurel','⚜':'laurel',
    '📖':'book','📒':'book','🎓':'book','💬':'speech','💭':'speech','🗣':'speech','🎤':'speech','⚠':'warn','🩸':'blood',
    '✉':'letter','📨':'letter','☠':'skull','💀':'skull','🦴':'skull','🏋':'fist','💪':'fist','🦾':'fist','🥊':'fist',
    '📛':'tag','🏷':'tag','🎬':'play','🎞':'play','🎥':'play','🌟':'star','⭐':'star','🍺':'mug','🔭':'eye','👁':'eye','🔍':'search',
    '🍷':'wine','🤝':'handshake','🧠':'mind','🖤':'heart','💥':'impact','💢':'impact','📈':'chart','📊':'chart','📉':'chart',
    '🌱':'sprout','🎯':'target','⏭':'ff','⏩':'ff','⏸':'pause','📰':'news','🗞':'news','🗄':'archive','📁':'archive','🕊':'dove',
    '⚡':'bolt','👍':'thumb','👎':'thumbdown','🛡':'shield','✏':'quill','🖋':'quill','⚕':'medic','🩹':'medic','🩼':'medic',
    '⚙':'gear','❄':'snow','📯':'horn','🕯':'candle','⚖':'scales','🔒':'lock','❔':'help','💎':'gem','⏳':'hourglass','⏱':'hourglass',
    '🏟':'arena','👤':'person'
  };
  // 아이콘이 아니라 모노크롬 글리프로 대체 (타이포그래피의 일부). ''=제거(구세이브의 장식 이모지)
  var TXT = { '⬆':'↑','⬇':'↓','➜':'→','🔴':'●','🟡':'●','🔷':'◆','🔶':'◆','⚑':'▸',
    '😡':'','😤':'','😨':'','😰':'','🤡':'','🙏':'','🫂':'','🌀':'','🪨':'','👋':'','🌊':'','🌫':'',
    '💨':'','🌾':'','🦵':'','🫁':'','📐':'','🔗':'','💦':'','😮':'','🏁':'','🔨':'' };

  var keys = Object.keys(EMO).concat(Object.keys(TXT));
  var RX = new RegExp('\\{([a-z]+)\\}|(' + keys.join('|') + ')\\uFE0F?', 'g');
  var SKIP = /^(SCRIPT|STYLE|TEXTAREA|TITLE)$/;

  function iconEl(name){
    var s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    s.setAttribute('class', 'ic');
    s.setAttribute('aria-hidden', 'true');
    var u = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    u.setAttribute('href', 'icons.svg#' + name);
    s.appendChild(u);
    return s;
  }

  function processTextNode(n){
    var t = n.nodeValue;
    if (!t) return;
    RX.lastIndex = 0;
    if (!RX.test(t)) return;
    RX.lastIndex = 0;
    var frag = document.createDocumentFragment(), last = 0, m;
    while ((m = RX.exec(t))){
      if (m.index > last) frag.appendChild(document.createTextNode(t.slice(last, m.index)));
      var rep = null;
      if (m[1]){ if (ICON[m[1]]) rep = iconEl(m[1]); }
      else {
        var raw = m[2];
        if (EMO[raw]) rep = iconEl(EMO[raw]);
        else if (raw in TXT) rep = document.createTextNode(TXT[raw]);
      }
      frag.appendChild(rep || document.createTextNode(m[0]));
      last = m.index + m[0].length;
    }
    if (last < t.length) frag.appendChild(document.createTextNode(t.slice(last)));
    n.parentNode.replaceChild(frag, n);
  }

  // ── 커스텀 컨트롤: .sw(스위치)·.seg(세그먼트) — 기본 checkbox/select 대체 ──
  // 기존 코드 호환을 위해 .checked / .value 프로퍼티 API를 그대로 제공한다.
  function initSwitch(el){
    if (el.__sw) return; el.__sw = true;
    var pre = Object.prototype.hasOwnProperty.call(el, 'checked') ? !!el.checked : null; // 초기화 전 설정 보존
    if (pre !== null) delete el.checked;
    var v = pre !== null ? pre : el.classList.contains('on');
    el.classList.toggle('on', v);
    Object.defineProperty(el, 'checked', {
      get: function(){ return v; },
      set: function(x){ v = !!x; el.classList.toggle('on', v); },
      configurable: true
    });
    el.addEventListener('click', function(){ el.checked = !v; el.dispatchEvent(new Event('change')); });
  }
  function initSeg(el){
    if (el.__seg) return; el.__seg = true;
    var pre = Object.prototype.hasOwnProperty.call(el, 'value') ? String(el.value) : null; // 초기화 전 설정 보존
    if (pre !== null) delete el.value;
    var cur = el.querySelector('button.cur');
    var v = pre !== null ? pre : (cur ? cur.dataset.v : (el.firstElementChild ? el.firstElementChild.dataset.v : ''));
    Object.defineProperty(el, 'value', {
      get: function(){ return v; },
      set: function(x){ v = String(x);
        for (var i = 0; i < el.children.length; i++) el.children[i].classList.toggle('cur', el.children[i].dataset.v === v); },
      configurable: true
    });
    el.value = v; // 초기 표시 동기화
    el.addEventListener('click', function(e){
      var b = e.target.closest('button[data-v]');
      if (!b || !el.contains(b)) return;
      el.value = b.dataset.v; el.dispatchEvent(new Event('change'));
    });
  }
  function initControls(root){
    if (root.nodeType !== 1) return;
    if (root.classList){ if (root.classList.contains('sw')) initSwitch(root); if (root.classList.contains('seg')) initSeg(root); }
    var sws = root.querySelectorAll ? root.querySelectorAll('.sw') : [];
    for (var i = 0; i < sws.length; i++) initSwitch(sws[i]);
    var segs = root.querySelectorAll ? root.querySelectorAll('.seg') : [];
    for (var j = 0; j < segs.length; j++) initSeg(segs[j]);
  }

  function scan(root){
    initControls(root);
    if (root.nodeType === 1) markInteractive(root);
    if (root.nodeType === 3){ if (root.parentNode && !SKIP.test(root.parentNode.tagName)) processTextNode(root); return; }
    if (root.nodeType !== 1 || SKIP.test(root.tagName)) return;
    var w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, { acceptNode: function(n){
      return (n.parentNode && SKIP.test(n.parentNode.tagName)) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
    }});
    var list = [];
    while (w.nextNode()) list.push(w.currentNode);
    for (var i = 0; i < list.length; i++) processTextNode(list[i]);
  }

  var mo = new MutationObserver(function(muts){
    for (var i = 0; i < muts.length; i++){
      var mu = muts[i];
      if (mu.type === 'characterData'){ scan(mu.target); continue; }
      for (var j = 0; j < mu.addedNodes.length; j++) scan(mu.addedNodes[j]);
    }
  });

  function boot(){
    if (document.body) scan(document.body);
    mo.observe(document.documentElement, { childList: true, subtree: true, characterData: true });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();

  // ── R3: 공용 클릭 반응 레이어 — 누르면 0.1초 안에 반드시 반응한다(소리+금빛 점멸) ──
  var _cac = null;
  function clickTone(){
    if (typeof window.sndOn === 'function' && !window.sndOn()) return;
    try{
      _cac = _cac || new (window.AudioContext||window.webkitAudioContext)();
      var t=_cac.currentTime, o=_cac.createOscillator(), g=_cac.createGain();
      o.type='triangle'; o.frequency.setValueAtTime(1400,t);
      o.frequency.exponentialRampToValueAtTime(700,t+.045);
      g.gain.setValueAtTime(.05,t); g.gain.exponentialRampToValueAtTime(.001,t+.05);
      o.connect(g).connect(_cac.destination); o.start(t); o.stop(t+.07);
    }catch(e){}
  }
  function spark(x,y){
    var host = document.getElementById('stage') || document.body;
    var r = host.getBoundingClientRect();
    var z = parseFloat(getComputedStyle(host).zoom) || 1;
    var d = document.createElement('div');
    d.style.cssText = 'position:absolute;z-index:97;width:8px;height:8px;margin:-4px 0 0 -4px;border-radius:50%;'+
      'border:2px solid var(--gold,#c9a84c);opacity:.75;pointer-events:none;'+
      'left:'+((x-r.x)/z)+'px;top:'+((y-r.y)/z)+'px;'+
      'transition:transform .38s cubic-bezier(.2,.7,.3,1), opacity .38s ease-out;';
    host.appendChild(d);
    requestAnimationFrame(function(){ d.style.transform='scale(3.4)'; d.style.opacity='0'; });
    setTimeout(function(){ d.remove(); }, 460);
  }
  document.addEventListener('pointerdown', function(e){
    var b = e.target.closest && e.target.closest('button, [onclick]');
    if (!b || b.disabled) return;
    clickTone();
    spark(e.clientX, e.clientY);
  }, true);

  // G7: 버튼이 아닌 클릭 타겟(카드·행)에 키보드 접근성 부여 — role/tabindex + Enter/Space 클릭.
  // 중첩된 [onclick]은 가장 바깥만 포커스 대상(카드 안 버튼이 이중 tabstop 되지 않게).
  function mark1(el){
    if (el.__kb || el.tagName === 'BUTTON' || el.hasAttribute('disabled')) return;
    if (el.querySelector('[onclick]')) return;                 // 바깥 컨테이너면 건너뜀(안쪽 실제 타겟만)
    if (!el.closest || !el.closest('#screens')) return;        // 화면 안 클릭 타겟만
    el.__kb = true;
    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '0');
    if (!el.hasAttribute('role')) el.setAttribute('role', 'button');
  }
  function markInteractive(root){
    if (root.nodeType !== 1) return;
    if (root.matches && root.matches('[onclick]:not(button)')) mark1(root);
    if (!root.querySelectorAll) return;
    var list = root.querySelectorAll('[onclick]:not(button)');
    for (var i = 0; i < list.length; i++) mark1(list[i]);
  }
  document.addEventListener('keydown', function(e){
    if (e.key !== 'Enter' && e.key !== ' ' && e.key !== 'Spacebar') return;
    var el = document.activeElement;
    if (!el || el.tagName === 'BUTTON' || el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') return;
    if (!el.matches || !el.matches('[onclick]')) return;
    e.preventDefault();
    el.click();
  });

  // 템플릿 리터럴에서 직접 아이콘 마크업이 필요할 때: `${IC('coin')}`
  window.IC = function(name){ return '<svg class="ic" aria-hidden="true"><use href="icons.svg#' + name + '"/></svg>'; };

  // ── 레거시 정규화 ──
  // 구세이브·구서버가 만든 문자열의 이모지를 {토큰}으로 바꾼다.
  // api() 응답을 이 관문에 통과시키면 클라이언트 파싱 로직은 {토큰}만 알면 된다.
  var RXS = new RegExp('(' + keys.join('|') + ')\\uFE0F?', 'g');
  // 구세이브의 서사 로그에는 「티투스이(가)」식 조사 병기가 박제돼 있다(서버가 고쳐진 뒤에도
  // 이미 저장된 문자열은 그대로다). 받침을 보고 맞는 조사 하나만 남긴다.
  var RXJ = /([\uAC00-\uD7A3\d])(?:이\(가\)|가\(이\)|을\(를\)|를\(을\)|은\(는\)|는\(은\)|과\(와\)|와\(과\))/g;
  function josaOf(ch, pair){
    // 숫자 끝 이름(「쌍검1」)은 읽는 소리로 — 1·3·6·7·8·0이 받침
    var jong = ch >= '0' && ch <= '9' ? '136780'.indexOf(ch) >= 0
             : (ch.charCodeAt(0) - 0xAC00) % 28 !== 0;
    return jong ? pair[0] : pair[1];
  }
  window.normLegacy = function(s){
    if (typeof s !== 'string') return s;
    s = s.replace(RXS, function(_, raw){ return EMO[raw] ? '{' + EMO[raw] + '}' : (raw in TXT ? TXT[raw] : raw); });
    return s.replace(RXJ, function(m, ch){
      var pair = m.indexOf('이') >= 0 || m.indexOf('가') >= 0 ? ['이','가']
               : m.indexOf('을') >= 0 || m.indexOf('를') >= 0 ? ['을','를']
               : m.indexOf('은') >= 0 || m.indexOf('는') >= 0 ? ['은','는'] : ['과','와'];
      return ch + josaOf(ch, pair);
    });
  };
  // 툴팁 등 속성 텍스트용 — 아이콘 토큰을 제거한 순수 문장 (속성은 DOM 관문이 못 고친다)
  window.plainTip = function(s){ return String(s == null ? '' : s).replace(/\{[a-z]+\}/g, '').replace(/  +/g, ' ').trim(); };
  window.normDeep = function(o){
    if (typeof o === 'string') return window.normLegacy(o);
    if (Array.isArray(o)){ for (var i = 0; i < o.length; i++) o[i] = window.normDeep(o[i]); return o; }
    if (o && typeof o === 'object'){ for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) o[k] = window.normDeep(o[k]); return o; }
    return o;
  };
})();
