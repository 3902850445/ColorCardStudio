
/**
 * 通道连通后拉取初始数据。
 * 由收到后端心跳时触发；也暴露为全局以便排查时手动调用。
 */
window.__cmBoot = function () {
  if (window.__cmBootDone) return;
  window.__cmBootDone = true;
  logInfo('通信', '开始拉取初始数据');
  rpc('getStandards')
    .then((list) => {
      logInfo('通信', `印刷标准已获取（${list.length} 套）`);
      applyStandards(list);
      return loadPresets();
    })
    .then(() => loadServerLogs())
    .catch((e) => logError('通信', '初始化请求失败', e.message));
};

/** 填充印刷标准下拉与说明。 */
function applyStandards(list) {
  state.standards = list || [];
  const sel = $('standardSelect');
  if (sel && state.standards.length) {
    sel.innerHTML = state.standards.map((s) =>
      `<option value="${esc(s.id)}">${esc(s.name)}（${s.tacLimit}%）</option>`).join('');
    state.standardId = state.standards[0].id;
    sel.value = state.standardId;
    const s0 = state.standards[0];
    $('tacNote').innerHTML =
      `<b>${esc(s0.name)}</b><br>总墨量上限 ${s0.tacLimit}% · 容差 ΔE ${s0.tolerableDeltaE}`;
    renderGrid();
  }
}


/* ColorMod 色卡工坊 —— 前端逻辑 */
'use strict';

// ============ 状态 ============
const state = {
  builtin: null,          // 内置色卡数据
  paletteKey: null,       // 当前色卡来源
  hueBin: null,           // 当前色相筛选
  standards: [],          // 印刷标准
  standardId: 'pso-coated-v3',
  presets: [],            // 自定义预设
  extract: null,          // 最近一次取色结果
  query: '',
};

// 色箱名称/代表色：由内置色卡文件下发的 HueBin 定义填充，
// 与后端 Core/HueBin.cs 保持同一口径，避免前后端各写一份而漂移。
// 兜底表仅在色卡文件缺失时使用。
const HUE_BIN_FALLBACK = {
  red: ['红色', '#e5484d'], orange: ['橙色', '#f76b15'], amber: ['琥珀', '#ffb224'],
  yellow: ['黄色', '#f5d90a'], lime: ['黄绿', '#99d52a'], green: ['绿色', '#30a46c'],
  emerald: ['翠绿', '#12a594'], cyan: ['青色', '#05a2c2'], sky: ['天蓝', '#0090ff'],
  blue: ['蓝色', '#3e63dd'], violet: ['紫罗兰', '#6e56cf'], magenta: ['洋红', '#d6409f'],
  gray: ['中性灰', '#8b96a4'],
};
const HUE_BIN_CN = Object.fromEntries(
  Object.entries(HUE_BIN_FALLBACK).map(([k, v]) => [k, v[0]]));
const HUE_BIN_HEX = Object.fromEntries(
  Object.entries(HUE_BIN_FALLBACK).map(([k, v]) => [k, v[1]]));

/** 用色卡文件里的 hueBins 定义覆盖兜底表。 */
function applyHueBins(bins) {
  if (!Array.isArray(bins) || !bins.length) return;
  for (const b of bins) {
    if (!b || !b.key) continue;
    if (b.name) HUE_BIN_CN[b.key] = b.name;
    if (b.swatch) HUE_BIN_HEX[b.key] = b.swatch;
  }
}

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// ============ 前端日志（同时输出到控制台与页面日志面板） ============
const LOG_BUFFER = [];
const LOG_MAX = 300;

function log(level, scope, message, detail) {
  const t = new Date();
  const stamp = `${String(t.getHours()).padStart(2, '0')}:` +
                `${String(t.getMinutes()).padStart(2, '0')}:` +
                `${String(t.getSeconds()).padStart(2, '0')}.` +
                `${String(t.getMilliseconds()).padStart(3, '0')}`;
  const entry = { time: stamp, level, scope, message: String(message ?? ''), detail: detail ?? '' };
  LOG_BUFFER.push(entry);
  if (LOG_BUFFER.length > LOG_MAX) LOG_BUFFER.shift();

  const line = `[${stamp}] [${level.toUpperCase()}] [${scope}] ${message}` +
               (detail ? `\n    ${detail}` : '');
  const tag = { error: 'error', warn: 'warn', info: 'info' }[level] || 'log';
  console[tag](line);
  renderLogPanel();
}

const logInfo = (s, m, d) => log('info', s, m, d);
const logWarn = (s, m, d) => log('warn', s, m, d);
const logError = (s, m, d) => log('error', s, m, d);

function renderLogPanel() {
  const box = $('logList');
  if (!box) return;

  // 错误徽标：始终反映全部缓冲中的错误/警告数
  const errs = LOG_BUFFER.filter((e) => e.level === 'error').length;
  const warns = LOG_BUFFER.filter((e) => e.level === 'warn').length;
  const badge = $('logBadge');
  if (badge) {
    badge.hidden = errs === 0;
    badge.textContent = errs;
  }

  if ($('view-logs')?.hidden) return;

  const order = { Debug: 0, Info: 1, Warn: 2, Error: 3 };
  const min = order[$('logFilter')?.value || 'Debug'] ?? 0;
  const list = LOG_BUFFER.filter((e) => (order[e.level] ?? 1) >= min).slice().reverse();

  box.innerHTML = list.length ? list.map((e) => `
    <div class="log-line ${e.level}">
      <span class="log-t">${e.time}</span>
      <span class="log-lv">${e.level.toUpperCase()}</span>
      <span class="log-sc" title="${esc(e.scope)}">${esc(e.scope)}</span>
      <span class="log-ms">${esc(e.message)}</span>
      ${e.detail ? `<div class="log-dt">${esc(e.detail)}</div>` : ''}
    </div>`).join('')
    : '<div class="empty"><p>暂无日志</p><span>操作后会自动记录</span></div>';
}

// ============ 前端消息追踪（与后端 Core/Trace.cs 格式对齐） ============
const FE_TRACE = [];
const FE_TRACE_MAX = 500;

function feTrace(channel, dir, summary, payload) {
  const body = payload == null ? '' : String(payload);
  const bytes = new TextEncoder().encode(body).length;
  const entry = {
    time: new Date().toISOString().slice(11, 23),
    dir, channel, summary,
    bytes,
    detail: body.length <= 200 ? body : body.slice(0, 200) + '…',
  };
  FE_TRACE.push(entry);
  if (FE_TRACE.length > FE_TRACE_MAX) FE_TRACE.shift();
  console.log(`[FE/${dir}] ${channel} ${bytes}B ${summary} :: ${entry.detail}`);
}

// ============ 后端桥接（Photino 4.x：window.external.sendMessage / receiveMessage） ============
let rpcSeq = 0;
const pending = new Map();

function backendAvailable() {
  return !!(window.external && typeof window.external.sendMessage === 'function');
}

function rpc(action, payload) {
  return new Promise((resolve, reject) => {
    const id = `r${++rpcSeq}`;
    if (!backendAvailable()) {
      const msg = '未连接到桌面后端（请通过 ColorMod.exe 启动，而非在浏览器中打开）';
      logError('通信', `请求 ${action} 失败：${msg}`);
      pending.set(id, { resolve, reject, timer: null });
      reject(new Error(msg));
      return;
    }
    pending.set(id, { resolve, reject, action });
    window.__cmPending = window.__cmPending || new Set();
    window.__cmPending.add(id);
    const wire = JSON.stringify({ id, action, payload });
    feTrace('rpc.send', 'OUT', `→ ${action}`, wire);
    logInfo('通信', `→ ${action}`, JSON.stringify(payload ?? {}).slice(0, 200));

    const timer = setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        if (window.__cmPending) window.__cmPending.delete(id);
        window.__cmPendingStuck = (window.__cmPending?.size || 0) > 0;
        const msg = `请求 ${action} 超时（30 秒无响应）`;
        feTrace('rpc.timeout', '--', `请求 ${action} 超时，发送于 ${new Date().toISOString().slice(11, 23)}`, '');
        logError('通信', msg);
        reject(new Error(msg));
      }
    }, 30000);

    try {
      feTrace('external.sendMessage', 'OUT', '调用原生发送', wire);
      window.external.sendMessage(wire);
      feTrace('external.sendMessage', 'OUT', '原生发送已返回', '');
    } catch (e) {
      feTrace('external.sendMessage', 'OUT', '原生发送异常: ' + e.message, '');
      clearTimeout(timer);
      pending.delete(id);
      logError('通信', `发送 ${action} 异常`, e.message);
      reject(new Error('无法与后端通信：' + e.message));
    }
  });
}

/**
 * 接收后端消息。
 * Photino 的约定：C# 侧 SendWebMessage(msg) 会调用前端的
 * window.external.receiveMessage(msg)。之前误用了自定义函数名，
 * 导致 C# -> JS 方向完全收不到响应（前端表现为「一直转圈/超时」）。
 */
window.__cmReceive = function (json) {
  let msg;
  try { msg = JSON.parse(json); }
  catch (e) {
    feTrace('JSON.parse', 'IN', '解析失败: ' + e.message, json);
    logError('通信', '后端响应不是合法 JSON', String(json).slice(0, 300));
    return;
  }
  feTrace('JSON.parse', 'IN', `解析成功 ok=${msg.ok} reqId=${msg.reqId}`, json);

  // Photino 可能重复投递同一条消息；reqId 处理过后直接忽略，
  // 否则同一个响应会被 resolve 多次或抛出「已 settle」的异常。
  const entry = msg.reqId ? pending.get(msg.reqId) : null;
  if (!entry) {
    if (!msg.ok && msg.error) logError('通信', '后端返回错误（无请求号）', msg.error);
    return;
  }
  pending.delete(msg.reqId);
  if (window.__cmPending) window.__cmPending.delete(msg.reqId);
  if (entry.timer) clearTimeout(entry.timer);

  if (msg.ok) {
    logInfo('通信', `← ${entry.action || msg.reqId} 成功`);
    entry.resolve(msg.data);
  } else {
    logError('通信', `← ${entry.action || msg.reqId} 失败`, msg.error);
    entry.reject(new Error(msg.error || '未知错误'));
  }
};

/**
 * 后端消息统一入口。
 * C# 侧通过独立发送线程投递裸 JSON（避免在消息回调内同步发送造成重入丢失），
 * 这里直接交给 __cmReceive 处理即可。
 */
window.__cmDispatch = function (payload) {
  if (typeof payload !== 'string') payload = String(payload);
  feTrace('__cmDispatch', 'IN', '收到后端投递', payload);
  window.__cmReceive(payload);
};

/**
 * 建立消息通道。
 * 关键：接收端必须挂在 window.external.receiveMessage 上，
 * 这是 Photino C# SendWebMessage 的唯一落点。
 */
(function setupBridge() {
  // 1) 兜底：若 Photino 未注入 external，用 chrome.webview 补一个
  if (!window.external || typeof window.external.sendMessage !== 'function') {
    if (window.chrome && window.chrome.webview) {
      window.external = {
        sendMessage: (m) => window.chrome.webview.postMessage(m),
      };
      logInfo('通信', '使用 chrome.webview 兼容通道');
    }
  }

  if (!window.external) {
    logError('通信', '未检测到 Photino 桥接，功能不可用');
    return;
  }

  // 2) 注册接收端（Photino C# -> JS 的落点）
  //    注意：保留 external 上已有的其它成员，只挂 receiveMessage
  window.external.receiveMessage = function (message) {
    feTrace('external.receiveMessage', 'IN', '原生投递到前端', message);
    window.__cmDispatch(message);
  };

  // 3) 同时监听 chrome.webview 的 message 事件，双保险
  if (window.chrome && window.chrome.webview &&
      typeof window.chrome.webview.addEventListener === 'function') {
    window.chrome.webview.addEventListener('message', (e) => {
      feTrace('chrome.webview', 'IN', 'webview 消息事件', e.data);
      window.__cmDispatch(e.data);
    });
  }

  // 4) 响应心跳：后端在窗口创建后会周期发 {"type":"ping"}
  //    收到即说明双向通道已通，主动拉取初始数据
  let pingCount = 0;
  const origReceive = window.__cmReceive;
  window.__cmReceive = function (msg) {
    try {
      if (msg && typeof msg === 'string' && msg.indexOf('"ping"') >= 0) {
        pingCount++;
        // 第 1 次心跳：确认通道可用，拉取初始数据
        if (pingCount === 1) {
          logInfo('通信', '收到后端心跳，通道已连通');
          window.__cmBoot();
        }
        // 后续心跳：若仍有请求悬空未决，说明早前的响应丢失了，补拉一次
        else if (pingCount === 3 && window.__pendingStuck) {
          logWarn('通信', '检测到早期响应丢失，重新拉取初始数据');
          window.__cmPending.clear();
          window.__cmBoot();
        }
        return;                       // 心跳无需走请求/响应
      }
    } catch { /* 忽略 */ }
    origReceive(msg);
  };

  logInfo('通信', '消息通道已建立');
})();

// ============ 色彩工具（与后端算法一致） ============
function hexToRgb(hex) {
  const h = hex.replace('#', '');
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}
function rgbToHex(r, g, b) {
  return '#' + [r, g, b].map((v) => v.toString(16).padStart(2, '0')).join('');
}
function rgbToHsv(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  if (d > 1e-12) {
    if (max === r) h = 60 * (((g - b) / d) % 6);
    else if (max === g) h = 60 * ((b - r) / d + 2);
    else h = 60 * ((r - g) / d + 4);
  }
  if (h < 0) h += 360;
  return [h, max <= 0 ? 0 : d / max, max];
}
function isDark(hex) {
  const [r, g, b] = hexToRgb(hex).map((v) => {
    v /= 255;
    return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
  });
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) < 0.35;
}
function contrastRatio(hexA, hexB) {
  const lum = (hex) => {
    const [r, g, b] = hexToRgb(hex).map((v) => {
      v /= 255;
      return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const l1 = lum(hexA), l2 = lum(hexB);
  const hi = Math.max(l1, l2), lo = Math.min(l1, l2);
  return (hi + 0.05) / (lo + 0.05);
}

// ============ CMYK（与后端同一算法 + TAC 限制） ============
function currentStandard() {
  return state.standards.find((s) => s.id === state.standardId) || { tacLimit: 300 };
}
function rgbToCmyk(r, g, b) {
  const tac = (currentStandard().tacLimit || 300) / 100;
  let rn = r / 255, gn = g / 255, bn = b / 255;
  let k = 1 - Math.max(rn, gn, bn);
  if (k < 1e-9) k = 0;
  const inv = 1 - k;
  let c = inv < 1e-9 ? 0 : (1 - rn - k) / inv;
  let m = inv < 1e-9 ? 0 : (1 - gn - k) / inv;
  let y = inv < 1e-9 ? 0 : (1 - bn - k) / inv;
  // UCR：CMY 过量转 K
  const minCmy = Math.min(c, m, y);
  if (minCmy > 0) {
    const amount = minCmy * 0.85;
    const kNew = Math.min(1, k + amount);
    const scale = (1 - kNew) < 1e-9 ? 0 : (1 - k) / (1 - kNew);
    c *= scale; m *= scale; y *= scale; k = kNew;
  }
  // TAC 限制
  const sum = c + m + y + k;
  if (sum > tac && sum > 1e-9) {
    const f = tac / sum;
    c *= f; m *= f; y *= f; k *= f;
  }
  const fx = (v) => Math.round(v * 1000) / 10;
  return { c: fx(c), m: fx(m), y: fx(y), k: fx(k), total: fx(c + m + y + k) };
}
const cmykStr = (v) => `${v.c} ${v.m} ${v.y} ${v.k}`;
const cmykCss = (v) => `cmyk(${v.c}% ${v.m}% ${v.y}% ${v.k}%)`;

// ============ 搜索解析 ============
function parseQuery(q) {
  const out = { text: q.trim().toLowerCase(), hex: [], rgb: null, cmyk: null };
  const hexRe = /#?([0-9a-f]{6}|[0-9a-f]{3})\b/gi;
  let m;
  while ((m = hexRe.exec(q)) !== null) {
    let h = m[1].toLowerCase();
    if (h.length === 3) h = h.split('').map((c) => c + c).join('');
    out.hex.push('#' + h);
  }
  const rgbRe = /rgba?\s*\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})/i;
  const rm = q.match(rgbRe);
  if (rm) {
    const [r, g, b] = [+rm[1], +rm[2], +rm[3]];
    if (r <= 255 && g <= 255 && b <= 255) out.rgb = rgbToHex(r, g, b);
  }
  const cmykRe = /cmyk?\s*\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})/i;
  const cm = q.match(cmykRe);
  if (cm) {
    const [c, mm, y, k] = [+cm[1], +cm[2], +cm[3], +cm[4]];
    if ([c, mm, y, k].every((v) => v <= 100)) {
      const r = 255 * (1 - c / 100) * (1 - k / 100);
      const g = 255 * (1 - mm / 100) * (1 - k / 100);
      const b = 255 * (1 - y / 100) * (1 - k / 100);
      out.cmyk = rgbToHex(Math.round(r), Math.round(g), Math.round(b));
    }
  }
  return out;
}

/** 匹配一条颜色：支持名称/HEX/RGB/CMYK 文本与颜色距离 */
function matchColor(item, q) {
  if (!q.text && !q.hex.length && !q.rgb && !q.cmyk) return 0;
  const name = (item.name || '').toLowerCase();
  const hex = item.hex.toLowerCase();

  if (name && q.text && name.includes(q.text)) {
    return name === q.text ? 1000 : (name.startsWith(q.text) ? 800 : 600);
  }
  if (q.hex.includes(hex)) return 950;
  if (q.rgb && q.rgb === hex) return 900;
  if (q.cmyk && q.cmyk === hex) return 850;
  if (q.text && hex.includes(q.text.replace(/[^0-9a-f]/g, ''))) return 500;
  return 0;
}

/**
 * 按感知距离（CIE76 Lab）排序，找出最接近目标色的若干颜色。
 * 用于精确值搜不到时给出「最接近的颜色」，避免用户一头雾水。
 */
function nearestColors(items, targetHex, limit = 12) {
  const [tr, tg, tb] = hexToRgb(targetHex);
  const tLab = rgbToLab(tr, tg, tb);
  return items
    .map((i) => {
      const [r, g, b] = hexToRgb(i.hex);
      const lab = rgbToLab(r, g, b);
      const d = Math.sqrt(
        Math.pow(lab[0] - tLab[0], 2) +
        Math.pow(lab[1] - tLab[1], 2) +
        Math.pow(lab[2] - tLab[2], 2));
      return { item: i, d };
    })
    .sort((a, b) => a.d - b.d)
    .slice(0, limit);
}

/** RGB -> Lab(D65 近似)，仅用于排序比较，精度足够。 */
function rgbToLab(r, g, b) {
  const f = (v) => {
    v /= 255;
    return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
  };
  const R = f(r), G = f(g), B = f(b);
  const x = (R * 0.4124 + G * 0.3576 + B * 0.1805) / 0.95047;
  const y = (R * 0.2126 + G * 0.7152 + B * 0.0722);
  const z = (R * 0.0193 + G * 0.1192 + B * 0.9505) / 1.08883;
  const t = (v) => (v > 0.008856 ? Math.cbrt(v) : 7.787 * v + 16 / 116);
  const fx = t(x), fy = t(y), fz = t(z);
  return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
}

// ============ 中英文色名 ============
/** 常见色相/色系的中英对照，用于给色卡生成可读中文名。 */
const CN_TONE = {
  50: '极浅', 100: '浅', 200: '偏浅', 300: '淡',
  400: '浅', 500: '标准', 600: '偏深', 700: '深',
  800: '浓', 900: '极浓', 950: '最深',
};

/** 设计体系里常见的色系英文名 -> 中文。 */
const CN_FAMILY = {
  red: '红', crimson: '绯红', scarlet: '猩红', ruby: '宝石红', tomato: '番茄红',
  orange: '橙', amber: '琥珀', gold: '金', yellow: '黄', lime: '青柠',
  green: '绿', emerald: '翠绿', jade: '翡翠', teal: '蓝绿', cyan: '青',
  aqua: '水青', sky: '天蓝', blue: '蓝', indigo: '靛蓝', violet: '紫罗兰',
  purple: '紫', plum: '梅紫', magenta: '洋红', fuchsia: '品红', pink: '粉',
  mauve: '藕荷', slate: '石板灰', gray: '灰', grey: '灰', sand: '沙色',
  sage: '灰绿', olive: '橄榄', brown: '棕', tan: '棕褐', white: '白', black: '黑',
  neutral: '中性', primary: '主色', secondary: '辅色', success: '成功',
  warning: '警告', danger: '危险', error: '错误', info: '信息', light: '浅色', dark: '深色',
};

/** 148 个 CSS 命名色的官方中文译名（W3C/CSS Color 常用译法）。 */
const CSS_CN_NAMES = {
  aliceblue: '爱丽丝蓝', antiquewhite: '古董白', aqua: '水色', aquamarine: '海蓝',
  azure: '天青', beige: '米色', bisque: '陶土粉', black: '黑色', blanchedalmond: '杏仁白',
  blue: '蓝色', blueviolet: '蓝紫', brown: '棕色', burlywood: '棕褐色',
  cadetblue: '军校蓝', chartreuse: '黄绿', chocolate: '巧克力色', coral: '珊瑚色',
  cornflowerblue: '矢车菊蓝', cornsilk: '玉米丝', crimson: '绯红', cyan: '青色',
  darkblue: '深蓝', darkcyan: '深青', darkgoldenrod: '深金菊', darkgray: '深灰',
  darkgreen: '深绿', darkkhaki: '深卡其', darkmagenta: '深洋红', darkolivegreen: '深橄榄绿',
  darkorange: '深橙', darkorchid: '深兰紫', darkred: '深红', darksalmon: '深鲑红',
  darkseagreen: '深海绿', darkslateblue: '深石板蓝', darkslategray: '深石板灰',
  darkturquoise: '深青绿', darkviolet: '深紫', deeppink: '深粉', deepskyblue: '深天蓝',
  dimgray: '暗灰', dodgerblue: '道奇蓝', firebrick: '耐火砖红', floralwhite: '花白',
  forestgreen: '森林绿', fuchsia: '品红', gainsboro: ' Gainsboro 灰', ghostwhite: '幽灵白',
  gold: '金色', goldenrod: '金菊', gray: '灰色', green: '绿色', greenyellow: '绿黄',
  honeydew: '蜜瓜绿', hotpink: '亮粉', indianred: '印度红', indigo: '靛蓝',
  ivory: '象牙白', khaki: '卡其', lavender: '薰衣草', lavenderblush: '淡紫红',
  lawngreen: '草坪绿', lemonchiffon: '柠檬挞', lightblue: '浅蓝', lightcoral: '浅珊瑚',
  lightcyan: '浅青', lightgoldenrodyellow: '浅金菊', lightgray: '浅灰',
  lightgreen: '浅绿', lightpink: '浅粉', lightsalmon: '浅鲑', lightseagreen: '浅海绿',
  lightskyblue: '浅天蓝', lightslategray: '浅石板灰', lightsteelblue: '浅钢蓝',
  lightyellow: '浅黄', lime: '青柠', limegreen: '酸橙绿', linen: '亚麻',
  magenta: '洋红', maroon: '栗色', mediumaquamarine: '中水绿', mediumblue: '中蓝',
  mediumorchid: '中兰紫', mediumpurple: '中紫', mediumseagreen: '中海绿',
  mediumslateblue: '中石板蓝', mediumspringgreen: '中春绿', mediumturquoise: '中青绿',
  mediumvioletred: '中紫红', midnightblue: '午夜蓝', mintcream: '薄荷奶白',
  mistyrose: '雾玫瑰', moccasin: '鹿皮', navajowhite: '纳瓦霍白', navy: '藏青',
  oldlace: '旧蕾丝', olive: '橄榄', olivedrab: '橄榄褐', orange: '橙色',
  orangered: '橙红', orchid: '兰紫', palegoldenrod: '淡金菊', palegreen: '淡绿',
  paleturquoise: '淡青绿', palevioletred: '淡紫红', papayawhip: '木瓜白',
  peachpuff: '桃霜', peru: '秘鲁棕', pink: '粉色', plum: '梅紫',
  powderblue: '粉蓝', purple: '紫色', rebeccapurple: 'Rebecca 紫', red: '红色',
  rosybrown: '玫瑰褐', royalblue: '宝蓝', saddlebrown: '马鞍棕', salmon: '鲑红',
  sandybrown: '沙棕', seagreen: '海绿', seashell: '贝壳', sienna: '赭色',
  silver: '银', skyblue: '天蓝', slateblue: '石板蓝', slategray: '石板灰',
  snow: '雪白', springgreen: '春绿', steelblue: '钢蓝', tan: '棕褐', teal: '蓝绿',
  thistle: '蓟色', tomato: '番茄红', turquoise: '青绿', violet: '紫罗兰',
  wheat: '麦色', white: '白色', whitesmoke: '白烟', yellow: '黄色', yellowgreen: '黄绿',
};

/**
 * 生成中文色名。优先查 CSS 命名色译名；否则按色相（英文名或取色结果）翻译，
 * 再叠加明度档位，例如 Tailwind 的 blue-600 -> 蓝 · 偏深。
 */
function colorCnName(hex, enName) {
  const en = String(enName || '').trim();
  const key = en.toLowerCase().replace(/[\s_-]/g, '');

  // 1) CSS 命名色直接用官方译名
  if (CSS_CN_NAMES[key]) return CSS_CN_NAMES[key];

  // 2) 解析形如 "blue-600" / "gray-100" / "primary40" 的命名
  const m = en.toLowerCase().match(/^([a-z]+)[-_]?(\d{1,4})$/);
  if (m && CN_FAMILY[m[1]]) {
    const tone = CN_TONE[Number(m[2])];
    return tone ? `${CN_FAMILY[m[1]]} · ${tone}` : CN_FAMILY[m[1]];
  }
  if (CN_FAMILY[key]) return CN_FAMILY[key];

  // 3) 按实际颜色归入色相族
  const bin = HUE_BIN_CN[binOfHex(hex)] || '色';
  return bin;
}

function binOfHex(hex) {
  const [r, g, b] = hexToRgb(hex);
  const [h, s, v] = rgbToHsv(r, g, b);
  if (s < 0.12 || v < 0.10) return 'gray';
  const bins = [['red',345,360],['red',0,15],['orange',15,45],['amber',45,70],
    ['yellow',70,100],['lime',100,140],['green',140,170],['emerald',170,195],
    ['cyan',195,220],['sky',220,245],['blue',245,270],['violet',270,295],['magenta',295,345]];
  for (const [k, a0, a1] of bins) if (h >= a0 && h < a1) return k;
  return 'gray';
}

// ============ 复制 ============
async function copy(text, label) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    document.body.removeChild(ta);
  }
  const what = label ? `已复制 ${label}` : '已复制到剪贴板';
  logInfo('剪贴板', `${what}：${String(text).slice(0, 60)}`);
  toast(what);
}
function toast(msg) {
  logInfo('提示', msg);
  const t = $('toast');
  t.textContent = msg;
  t.hidden = false;
  clearTimeout(t._timer);
  t._timer = setTimeout(() => { t.hidden = true; }, 1600);
}

// ============ 渲染：色卡库 ============
function allItems() {
  if (!state.builtin || !state.builtin.palettes) return [];
  if (state.view === 'presets') return state.presets.flatMap((p) =>
    p.colors.map((c) => ({ ...c, palette: p.name })));
  // 「全部色卡」模式：跨来源汇总，便于全库搜索某个色值
  if (state.paletteKey === '__all__') {
    const out = [];
    for (const [name, p] of Object.entries(state.builtin.palettes)) {
      for (const c of p.colors || []) out.push({ ...c, palette: name });
    }
    return out;
  }
  const p = state.builtin.palettes[state.paletteKey];
  return p ? p.colors : [];
}

function renderPaletteTabs() {
  const tabs = $('paletteTabs');
  if (!state.builtin || !state.builtin.palettes) {
    if (tabs) tabs.innerHTML = '';
    return;
  }
  const names = Object.keys(state.builtin.palettes);
  const total = names.reduce((s, n) => s + (state.builtin.palettes[n].count || 0), 0);

  // 「全部色卡」置顶，便于跨色卡搜索某个色值
  let html = `<button class="ptab ${state.paletteKey === '__all__' ? 'active' : ''}" data-key="__all__">
      全部色卡<span class="cnt">${total}</span></button>`;
  html += names.map((n) => {
    const p = state.builtin.palettes[n];
    return `<button class="ptab ${n === state.paletteKey ? 'active' : ''}" data-key="${esc(n)}">
      ${esc(n)}<span class="cnt">${p.count}</span></button>`;
  }).join('');

  tabs.innerHTML = html;
  tabs.querySelectorAll('.ptab').forEach((b) => {
    b.onclick = () => {
      state.paletteKey = b.dataset.key;
      renderPaletteTabs();
      renderHueChips();
      renderGrid();
      logInfo('色卡', `切换到「${b.dataset.key === '__all__' ? '全部色卡' : b.dataset.key}」`);
    };
  });
}

function renderHueChips() {
  const items = allItems();
  const box = $('hueChips');
  if (!box) return;
  const counts = {};
  items.forEach((i) => { counts[i.hueBin] = (counts[i.hueBin] || 0) + 1; });
  const keys = Object.keys(HUE_BIN_CN).filter((k) => counts[k]);
  $('hueChips').innerHTML = keys.map((k) => `
    <button class="hchip ${state.hueBin === k ? 'active' : ''}" data-bin="${k}">
      <span class="dot" style="background:${HUE_BIN_HEX[k]}"></span>${HUE_BIN_CN[k]}
      <span class="cnt">${counts[k]}</span>
    </button>`).join('');
  $('hueChips').querySelectorAll('.hchip').forEach((b) => {
    b.onclick = () => {
      state.hueBin = state.hueBin === b.dataset.bin ? null : b.dataset.bin;
      renderHueChips(); renderGrid();
    };
  });
}

function renderGrid() {
  if (!state.builtin) {
    logWarn('色卡', '内置色卡尚未载入，网格不渲染');
    return;
  }
  const grid = $('paletteGrid');
  const empty = $('libraryEmpty');
  const items = allItems();
  const q = parseQuery(state.query);

  let list = items;
  if (state.hueBin) list = list.filter((i) => i.hueBin === state.hueBin);

  let nearestNote = '';
  const hasQuery = q.text || q.hex.length || q.rgb || q.cmyk;
  if (hasQuery) {
    const scored = list.map((i) => ({ item: i, score: matchColor(i, q) }))
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score);
    list = scored.map((x) => x.item);

    // 精确匹配为空但查询里带明确色值时，给出最接近的颜色而不是空白
    if (list.length === 0) {
      const target = q.hex[0] || q.rgb || q.cmyk;
      if (target) {
        list = nearestColors(items, target).map((x) => x.item);
        nearestNote = `未找到完全一致的颜色，以下是最接近的 ${list.length} 个`;
        logInfo('搜索', `无精确匹配，已给出最接近的颜色（目标 ${target}）`);
      }
    }
  }

  empty.hidden = list.length > 0;
  $('statText').textContent = `${list.length} / ${items.length} 色`;

  const isAll = state.paletteKey === '__all__';
  const p = isAll ? null : state.builtin?.palettes[state.paletteKey];
  $('paletteMeta').innerHTML = state.query
    ? (nearestNote
        ? `${esc(nearestNote)}（目标 <code>${esc(q.hex[0] || q.rgb || q.cmyk)}</code>）`
        : `匹配 “${esc(state.query)}” 的颜色${isAll ? '（已搜索全部色卡）' : ''}`)
    : (isAll ? '已汇总全部色卡，可直接搜索任意色值；同名色来自不同来源'
             : (p && p.source ? `来源：${esc(p.source)}` : ''));

  grid.innerHTML = list.map((i) => {
    const [r, g, b] = hexToRgb(i.hex);
    const cmyk = rgbToCmyk(r, g, b);
    const cn = colorCnName(i.hex, i.name);
    const src = isAll && i.palette
      ? `<div class="swatch-src" title="来自 ${esc(i.palette)}">${esc(i.palette)}</div>` : '';
    // 每个分量单独可点复制；标签与整行也可点（复制整值）
    const num = (v, channel, color) =>
      `<b class="num" style="--ch:${color}" data-ch="${channel}" title="点击复制 ${channel} 值">${v}</b>`;
    return `<div class="swatch" data-hex="${i.hex}" data-name="${esc(i.name)}">
      <div class="swatch-chip" style="background:${i.hex}">${src}</div>
      <div class="swatch-name" title="${esc(i.name)}">
        <span class="nm-cn">${esc(cn)}</span>
        <span class="nm-en">${esc(i.name)}</span>
      </div>
      <div class="swatch-vals">
        <div class="val-row cmyk" data-copy="cmyk" title="点击复制整组 CMYK">
          <span class="val-label">CMYK</span>
          <span class="nums">${num(cmyk.c, 'C', 'var(--cmyk-c)')}${num(cmyk.m, 'M', 'var(--cmyk-m)')}${num(cmyk.y, 'Y', 'var(--cmyk-y)')}${num(cmyk.k, 'K', 'var(--cmyk-k)')}</span>
        </div>
        <div class="val-row" data-copy="hex" title="点击复制 HEX（颜色代码整体复制）">
          <span class="val-label">HEX</span><span class="code">${i.hex}</span></div>
        <div class="val-row" data-copy="rgb" title="点击复制整组 RGB">
          <span class="val-label">RGB</span>
          <span class="nums">${num(r, 'R', 'var(--cmyk-c)')}${num(g, 'G', 'var(--cmyk-m)')}${num(b, 'B', 'var(--cmyk-y)')}</span>
        </div>
      </div>
    </div>`;
  }).join('');

  grid.querySelectorAll('.swatch').forEach((el) => {
    // 单个分量：点哪个数字就只复制哪个
    el.querySelectorAll('.num').forEach((n) => {
      n.onclick = (ev) => {
        if (ev && ev.stopPropagation) ev.stopPropagation();
        copy(n.textContent.trim(), `${n.dataset.ch} 分量`);
      };
    });
    el.querySelectorAll('.val-row').forEach((row) => {
      row.onclick = (ev) => {
        if (ev && ev.stopPropagation) ev.stopPropagation();
        const hex = el.dataset.hex;
        const [r, g, b] = hexToRgb(hex);
        const kind = row.dataset.copy;
        const map = {
          cmyk: [cmykStr(rgbToCmyk(r, g, b)), 'CMYK'],
          hex: [hex, 'HEX'],
          rgb: [`rgb(${r}, ${g}, ${b})`, 'RGB'],
        };
        copy(map[kind][0], map[kind][1]);
      };
    });
    el.onclick = () => openDetail(el.dataset.hex, el.dataset.name);
  });
}

// ============ 抽屉详情 ============
async function openDetail(hex, name) {
  logInfo('详情', `打开 ${hex}${name ? ' / ' + name : ''}`);
  const [r, g, b] = hexToRgb(hex);
  let d;
  try {
    d = await rpc('convertColor', { hex, standardId: state.standardId });
  } catch (e) {
    toast('换算失败: ' + e.message);
    return;
  }
  const std = currentStandard();
  const dark = isDark(hex);
  const over = d.tacExceeded;

  const inkBar = (label, val, color) => `
    <div class="ink-row">
      <span class="lbl" style="color:${color}">${label}</span>
      <span class="track"><i style="width:${Math.min(100, val)}%;background:${color}"></i></span>
      <span class="n num" data-ch="${label}" title="点击复制 ${label} 值">${val}%</span>
    </div>`;

  const items = [
    ['CMYK', cmykStr(d.cmyk)],
    ['HEX', d.hex],
    ['RGB', `${d.rgb.R}, ${d.rgb.G}, ${d.rgb.B}`],
    ['CMYK%', `cmyk(${d.cmyk.C}%, ${d.cmyk.M}%, ${d.cmyk.Y}%, ${d.cmyk.K}%)`],
    ['HSL', `${Math.round(d.hsl.H)}°, ${Math.round(d.hsl.S)}%, ${Math.round(d.hsl.L)}%`],
    ['LAB', `L*${d.lab.L.toFixed(1)} a*${d.lab.A.toFixed(1)} b*${d.lab.B.toFixed(1)}`],
    ['Lab-hex', labToHex(d.lab)],
  ];

  $('drawerBody').innerHTML = `
    <div class="d-chip" style="background:${hex}"></div>
    <div class="d-name">${esc(colorCnName(hex, name))}</div>
    <div class="d-name-en">${esc(name || hex)}</div>
    <div class="d-fam">${esc(std.name || '')} · 纸张白点 L*${std.paperWhite?.L ?? '—'}</div>

    <div class="tac-badge ${over ? 'over' : 'ok'}">
      <span>${over ? '✕ 超出' : '✓ 合规'}</span>
      <span>总墨量 <b>${d.cmyk.totalInk.toFixed(0)}%</b> / 上限 ${std.tacLimit}%</span>
    </div>
    ${d.issues.length ? `<div class="warn-list">${d.issues.map((x) => `<div>${esc(x)}</div>`).join('')}</div>` : ''}

    <div class="d-section">
      <h5>色值（点击整行复制全部；单独复制见下方分色墨量）</h5>
      <div class="d-list">
        ${items.map(([l, v]) => `<div class="d-item" data-v="${esc(v)}">
          <span class="lbl">${l}</span><span class="val">${esc(v)}</span><span class="cp">复制</span></div>`).join('')}
      </div>
    </div>

    <div class="d-section">
      <h5>分色墨量</h5>
      ${inkBar('C', d.cmyk.C, 'var(--cmyk-c)')}
      ${inkBar('M', d.cmyk.M, 'var(--cmyk-m)')}
      ${inkBar('Y', d.cmyk.Y, 'var(--cmyk-y)')}
      ${inkBar('K', d.cmyk.K, 'var(--cmyk-k)')}
      <div class="hint">点击任一分量数值可单独复制该通道的墨量百分比</div>
    </div>

    <div class="d-section">
      <h5>可读性</h5>
      <div class="d-list">
        <div class="d-item"><span class="lbl">白底</span>
          <span class="val">对比度 ${d.contrastOnWhite}:1 ${d.contrastOnWhite >= 4.5 ? '✓ AA' : '✕ 不足'}</span></div>
        <div class="d-item"><span class="lbl">建议</span>
          <span class="val">${d.suggestBlackText ? '底色浅，正文用黑色 K100%' : '底色深，正文用白色'}</span></div>
      </div>
    </div>`;

  $('drawerBody').querySelectorAll('.d-item[data-v]').forEach((el) => {
    el.onclick = () => copy(el.dataset.v, el.querySelector('.lbl').textContent);
  });
  // 抽屉内的分色墨量数值：单独点击只复制该通道
  $('drawerBody').querySelectorAll('.ink-row .num').forEach((n) => {
    n.onclick = () => copy(n.textContent.replace('%', '').trim(), `${n.dataset.ch} 墨量`);
  });
  $('drawer').hidden = false;
}

function labToHex(lab) {
  // 简化：Lab(D50) -> sRGB，用于展示
  const f = (t) => (t > 6 / 29 ? t * t * t : (108 / 841) * (t - 4 / 29));
  const wy = [0.96422, 1.0, 0.82521];
  const fx = (lab.L + 16) / 116 + lab.A / 500;
  const fy = (lab.L + 16) / 116;
  const fz = (lab.L + 16) / 116 - lab.B / 200;
  const X = wy[0] * f(fx), Y = wy[1] * f(fy), Z = wy[2] * f(fz);
  const bradford = [[1.0478112, 0.0228866, -0.0501270], [0.0295424, 0.9904844, -0.0170491],
                    [-0.0092345, 0.0150436, 0.7521316]];
  const inv = [[0.9555766, -0.0230393, 0.0631636], [-0.0282895, 1.0099416, 0.0210077],
               [0.0122982, -0.0204830, 1.3299098]];
  const d65 = inv.map((r) => r[0] * X + r[1] * Y + r[2] * Z);
  const m = [[3.1338561, -1.6168667, -0.4906146], [-0.9787684, 1.9161415, 0.0334540],
             [0.0719453, -0.2289914, 1.4052427]];
  const lin = m.map((r) => r[0] * d65[0] + r[1] * d65[1] + r[2] * d65[2]);
  const enc = lin.map((v) => {
    v = Math.max(0, Math.min(1, v));
    const s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.pow(v, 1 / 2.4) - 0.055;
    return Math.round(s * 255);
  });
  return rgbToHex(enc[0], enc[1], enc[2]);
}

// ============ 取色 ============
/**
 * 取色状态：pending 表示正在处理，必须等后端明确结果。
 * 绝不能在没有响应时判定为「失败」——那会让用户误以为功能坏了。
 */
const PICK = { busy: false };

function setPickStatus(kind, text) {
  const el = $('pickStatus');
  if (!el) return;
  if (!kind) { el.hidden = true; el.textContent = ''; return; }
  el.hidden = false;
  el.className = 'dz-status ' + kind;
  el.textContent = text;
}

/**
 * 打开图片选择器。
 * 走后端原生对话框（Photino ShowOpenFileAsync）：能拿到完整路径，
 * 且是异步的不会阻塞界面。
 * 解析期间禁用按钮，避免重复触发导致反复弹窗。
 */
async function pickFile(kind = 'image') {
  if (PICK.busy) {
    logWarn('取色', '上一张图片仍在解析中，请稍候');
    return;
  }
  PICK.busy = true;
  const btn = $('pickFileBtn');
  const label = btn ? btn.textContent : '';
  if (btn) { btn.disabled = true; btn.textContent = '处理中…'; }
  setPickStatus('busy', '正在等待选择图片…');

  try {
    const r = await rpc('pickFile', {
      kind,
      title: '选择图片以提取颜色',
    });
    if (!r || !r.ok || !r.path) {
      // 用户取消，不是错误
      logInfo('取色', '已取消选择');
      setPickStatus(null);
      return;
    }
    logInfo('取色', `已选择 ${r.path}`);
    setPickStatus('busy', `正在解析：${r.path.split(/[\\/]/).pop()}`);
    // 解析结果由 extractFrom 负责更新状态
    await extractFrom(r.path);
  } catch (e) {
    logError('取色', '选择图片失败', e.message);
    setPickStatus('error', '无法打开图片：' + e.message);
  } finally {
    PICK.busy = false;
    if (btn) { btn.disabled = false; btn.textContent = label; }
  }
}

const PALETTE_EXTS = ['ase', 'aseu', 'gpl', 'css', 'json', 'hex', 'txt'];

async function extractFrom(path) {
  const name = String(path || '').split(/[\\/]/).pop() || path;
  logInfo('取色', `开始解析：${name}`);
  const info = $('extractInfo');
  info.classList.add('show');
  info.innerHTML = `<div class="ei-row"><span>正在解析 ${esc(name)}，请稍候…</span></div>`;
  setPickStatus('busy', `正在解析：${name}`);

  try {
    const r = await rpc('extract', {
      path, maxColors: +$('colorCount').value, standardId: state.standardId,
    });

    // 到这里才是后端明确返回了结果
    state.extract = r;
    renderExtract(r);
    setPickStatus('ok', `已解析 ${r.colors.length} 个颜色（耗时数据见下方）`);
    logInfo('取色', `完成：${r.colors.length} 色，空间 ${r.sourceColorSpace}` +
                    (r.warnings?.length ? `（${r.warnings.join('；')}）` : ''));
    return r;
  } catch (e) {
    // 只有后端明确返回错误，才判定为失败。
    // 超时属于「未拿到结果」，仍按处理中提示，不谎报失败。
    const isTimeout = /超时/.test(e.message || '');
    if (isTimeout) {
      logWarn('取色', `${name} 解析超时，仍在等待后端结果`, e.message);
      info.innerHTML = `<div class="ei-row"><span>解析耗时较长，仍在等待后端返回…</span></div>`;
      setPickStatus('busy', `${name}：仍在解析中，请稍候`);
    } else {
      logError('取色', `解析失败：${name}`, e.message);
      info.innerHTML = `<div class="ei-row"><span style="color:var(--danger)">解析失败：${esc(e.message)}</span></div>`;
      setPickStatus('error', `解析失败：${e.message}`);
    }
    return null;
  }
}

function renderExtract(r) {
  const std = currentStandard();
  const info = $('extractInfo');
  info.classList.add('show');
  info.innerHTML = `
    <div class="ei-row"><span>文件</span><span>${esc(r.sourceFile || '')}</span></div>
    <div class="ei-row"><span>尺寸</span><span>${r.imageWidth} × ${r.imageHeight}</span></div>
    <div class="ei-row"><span>色彩空间</span><span>${esc(r.sourceColorSpace)}</span></div>
    <div class="ei-row"><span>分析像素</span><span>${r.totalPixelsAnalyzed.toLocaleString()}</span></div>
    <div class="ei-row"><span>平均总墨量</span><span>${Number(r.totalInkCoverage || 0).toFixed(1)}% / ${std.tacLimit}%</span></div>
    ${r.warnings.length ? `<div class="warn-list">${r.warnings.map((w) => `<div>${esc(w)}</div>`).join('')}</div>` : ''}`;

  $('extractGrid').innerHTML = r.colors.map((c) => {
    // 后端以 camelCase 输出（PropertyNamingPolicy），字段名必须小写开头
    const cmyk = { c: c.c, m: c.m, y: c.y, k: c.k, total: c.c + c.m + c.y + c.k };
    const over = cmyk.total > std.tacLimit + 0.05;
    return `<div class="ex-card" data-hex="${c.hex}">
      <div class="ex-chip" style="background:${c.hex}">
        <span class="ex-cov">${c.coverage.toFixed(1)}%</span></div>
      <div class="ex-body">
        <div class="val-row cmyk" data-copy="cmyk"><span class="val-label">CMYK</span><span>${cmykStr(cmyk)}</span></div>
        ${over ? `<div class="val-row" style="color:var(--danger)"><span class="val-label">TAC</span><span>超限 ${cmyk.total.toFixed(0)}%</span></div>` : ''}
        <div class="val-row" data-copy="hex"><span class="val-label">HEX</span><span>${c.hex}</span></div>
        <div class="val-row" data-copy="rgb"><span class="val-label">RGB</span><span>${c.r} ${c.g} ${c.b}</span></div>
      </div>
    </div>`;
  }).join('');

  $('extractGrid').querySelectorAll('.ex-card').forEach((el) => {
    el.querySelectorAll('.val-row').forEach((row) => {
      row.onclick = (ev) => {
        if (ev && ev.stopPropagation) ev.stopPropagation();
        const [r2, g2, b2] = hexToRgb(el.dataset.hex);
        const kind = row.dataset.copy;
        const map = {
          cmyk: [cmykStr(rgbToCmyk(r2, g2, b2)), 'CMYK'],
          hex: [el.dataset.hex, 'HEX'],
          rgb: [`rgb(${r2}, ${g2}, ${b2})`, 'RGB'],
        };
        copy(map[kind][0], map[kind][1]);
      };
    });
    el.onclick = () => openDetail(el.dataset.hex, '提取色');
  });
}

async function saveExtractAsPreset() {
  if (!state.extract?.colors?.length) return toast('请先提取颜色');
  const name = $('presetName').value.trim() ||
    `取色预设 ${new Date().toLocaleString('zh-CN', { hour12: false }).replace(/\//g, '-')}`;
  const colors = state.extract.colors.map((c) => {
    const [h, s, v] = rgbToHsv(c.r, c.g, c.b);
    let bin = 'gray';
    if (s >= 0.12 && v >= 0.10) {
      const bins = [['red', 345, 360], ['red', 0, 15], ['orange', 15, 45], ['amber', 45, 70],
        ['yellow', 70, 100], ['lime', 100, 140], ['green', 140, 170], ['emerald', 170, 195],
        ['cyan', 195, 220], ['sky', 220, 245], ['blue', 245, 270], ['violet', 270, 295],
        ['magenta', 295, 345]];
      for (const [k, a0, a1] of bins) {
        if ((h >= a0 && h < a1) || (a0 === 0 && h < a1)) { bin = k; break; }
      }
    }
    return { name: '', hex: c.hex, hueBin: bin, family: '' };
  });
  await rpc('savePalette', { name, colors, standardId: state.standardId, source: 'custom' });
  $('presetName').value = '';
  await loadPresets();
  toast(`已保存预设「${name}」`);
  switchView('presets');
}

async function copyAllCmyk() {
  if (!state.extract?.colors?.length) return toast('请先提取颜色');
  const lines = state.extract.colors.map((c) =>
    `${c.c} ${c.m} ${c.y} ${c.k}  #${c.hex.replace('#', '').toUpperCase()}  (${c.coverage.toFixed(1)}%)`);
  await copy(lines.join('\n'), `${lines.length} 条 CMYK`);
}

// ============ 预设 ============
async function loadPresets() {
  state.presets = await rpc('listPalettes');
  $('presetCount').textContent = state.presets.length;
  renderPresets();
}

function renderPresets() {
  const wrap = $('presetList');
  $('presetEmpty').hidden = state.presets.length > 0;
  wrap.innerHTML = state.presets.map((p) => `
    <div class="preset-card">
      <div class="preset-top">
        <h4>${esc(p.name)}</h4>
        <span class="meta">${p.colors.length} 色 · ${new Date(p.createdAt).toLocaleDateString('zh-CN')}</span>
        <div class="acts">
          <button class="btn ghost sm" data-act="export" data-id="${p.id}">导出</button>
          <button class="btn ghost sm" data-act="delete" data-id="${p.id}">删除</button>
        </div>
      </div>
      <div class="preset-strip">
        ${p.colors.map((c) => `<div class="pstrip-item" style="background:${c.hex}"
          data-hex="${c.hex}" title="${esc(c.name || c.hex)}"></div>`).join('')}
      </div>
    </div>`).join('');

  wrap.querySelectorAll('.preset-card').forEach((card) => {
    card.querySelectorAll('.pstrip-item').forEach((s) => {
      s.onclick = () => openDetail(s.dataset.hex, '预设色');
    });
    card.querySelectorAll('button[data-act]').forEach((b) => {
      b.onclick = async () => {
        const id = b.dataset.id;
        const pal = state.presets.find((x) => x.id === id);
        if (b.dataset.act === 'delete') {
          if (!confirm(`确定删除预设「${pal?.name}」？`)) return;
          await rpc('deletePalette', { id });
          await loadPresets();
          toast('已删除');
        } else {
          const res = await rpc('exportPalettes', { ids: [id] });
          const path = await saveViaDialog(res.fileName, res.json);
          if (path) toast(`已导出「${pal?.name}」`);
        }
      };
    });
  });
}

async function importFile(path) {
  try {
    // 后端已完成解析与入库（多预设逐个保存，各自保留原名）
    const r = await rpc('importFile', { path });
    await loadPresets();
    const extra = r.paletteCount > 1 ? `，${r.paletteCount} 个预设` : '';
    toast(`已导入 ${r.count} 色${extra}（${r.format}）`);
    switchView('presets');
  } catch (e) {
    toast('导入失败：' + e.message);
  }
}

/** 通过后端原生保存对话框写文件（WebView 的 blob 下载常被拦截）。 */
/**
 * 保存文本到文件。
 * 不用原生保存对话框（会在 UI 线程上阻塞），改用 WebView 的下载机制：
 * Blob + 临时链接触发下载，WebView2 会弹出「另存为」由用户决定位置。
 */
async function saveViaDialog(defaultName, text, filter) {
  const type = /\.txt$/.test(defaultName) ? 'text/plain' : 'application/json';
  const blob = new Blob([text], { type: type + ';charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = defaultName;
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  setTimeout(() => {
    URL.revokeObjectURL(url);
    document.body.removeChild(a);
  }, 1500);
  logInfo('导出', `已触发下载：${defaultName}（${text.length} 字符）`);
  return defaultName;
}

async function exportAll() {
  if (!state.presets.length) return toast('还没有可导出的预设');
  const res = await rpc('exportPalettes', { ids: [] });
  const path = await saveViaDialog(res.fileName, res.json);
  if (path) toast(`已导出 ${res.count} 个预设`);
}

/** 导出当前视图正在看的这组颜色，便于分享单组色卡。 */
async function exportCurrentView() {
  const items = allItems();
  if (!items.length) return toast('当前没有可导出的颜色');
  const seen = new Set();
  const colors = items.filter((i) => {
    const k = i.hex.toLowerCase();
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  }).map((i) => ({ name: i.name, hex: i.hex.toLowerCase(), hueBin: i.hueBin, family: i.family || '' }));

  const name = state.view === 'presets' ? '当前预设组' : (state.paletteKey || '色卡');
  const payload = JSON.stringify([{
    name, source: 'export', standardId: state.standardId, colors,
  }], null, 2);

  const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
  const path = await saveViaDialog(`colormod-${stamp}.json`, payload);
  if (path) toast(`已导出 ${colors.length} 色`);
}

// ============ 印刷标准 ============
function renderStandards() {
  const box = $('stdGrid');
  if (!box || !state.standards.length) {
    if (box) box.innerHTML = '<div class="empty"><p>暂无印刷标准</p>' +
      '<span>未能连接后端加载标准数据</span></div>';
    return;
  }
  box.innerHTML = state.standards.map((s) => {
    const cur = s.id === state.standardId;
    const solid = (lab, code) => {
      const [r, g, b] = hexToRgb(labToHex(lab));
      const hx = rgbToHex(r, g, b);
      return `<div class="solid" style="background:${hx};color:${isDark(hx) ? '#fff' : '#000'}">${code}</div>`;
    };
    return `<div class="std-card ${cur ? 'current' : ''}" data-id="${s.id}">
      <div class="std-name">${esc(s.name)}${cur ? '<span class="std-current-tag">当前</span>' : ''}</div>
      <div class="std-sub">${esc(s.paperType)} · ${esc(s.region)} · ${esc(s.process)}</div>
      <div class="std-rows">
        <div class="std-row"><span>总墨量上限</span><span>${s.tacLimit}%</span></div>
        <div class="std-row"><span>平均容差 ΔE</span><span>${s.tolerableDeltaE}</span></div>
        <div class="std-row"><span>单点最大 ΔE</span><span>${s.maxDeltaE}</span></div>
        <div class="std-row"><span>纸张白点</span><span>L*${s.paperWhite.L} a*${s.paperWhite.A} b*${s.paperWhite.B}</span></div>
      </div>
      <div class="solids">
        ${solid(s.solidC, 'C')}${solid(s.solidM, 'M')}${solid(s.solidY, 'Y')}${solid(s.solidK, 'K')}
      </div>
      <div class="std-note">${esc(s.note)}</div>
    </div>`;
  }).join('');

  $('stdGrid').querySelectorAll('.std-card').forEach((card) => {
    card.onclick = () => selectStandard(card.dataset.id);
  });
}

function selectStandard(id) {
  state.standardId = id;
  $('standardSelect').value = id;
  renderStandards();
  const s = currentStandard();
  $('tacNote').innerHTML = `<b>${esc(s.name)}</b><br>总墨量上限 ${s.tacLimit}% · 容差 ΔE ${s.tolerableDeltaE}`;
  renderGrid();
  if (state.extract) renderExtract(state.extract);
  toast('已切换到 ' + s.name);
}

// ============ 视图切换 ============
function switchView(v) {
  state.view = v;
  document.querySelectorAll('.nav-item').forEach((b) =>
    b.classList.toggle('active', b.dataset.view === v));
  ['library', 'picker', 'presets', 'standards', 'logs'].forEach((x) => {
    const el = $(`view-${x}`);
    if (el) el.hidden = x !== v;
  });
  if (v === 'library') {
    try { renderPaletteTabs(); renderHueChips(); renderGrid(); }
    catch (e) { logError('界面', '渲染色卡库出错', e.message); }
  }
  if (v === 'presets') renderPresets();
  if (v === 'standards') renderStandards();
  if (v === 'logs') {
    renderLogPanel();
    loadServerLogs().catch(() => { /* 已在日志中记录 */ });
    if (!$('tracePanel').hidden) loadTrace();
  }
  logInfo('界面', `切换到「${v}」`);
}

// ============ 初始化 ============
/** 独立执行一步初始化，失败只记录日志与提示，不中断后续步骤。 */
async function step(name, fn, { required = false } = {}) {
  try {
    await fn();
    logInfo('初始化', `${name} 完成`);
    return true;
  } catch (e) {
    const msg = e && e.message ? e.message : String(e);
    logError('初始化', `${name} 失败`, msg);
    toast(`${name} 失败：${msg}`);
    if (required) {
      $('libraryEmpty').hidden = false;
      $('libraryEmpty').innerHTML =
        `<p>${esc(name)}失败</p><span>${esc(msg)}</span>`;
    }
    return false;
  }
}

async function init() {
  logInfo('初始化', `页面就绪，UA=${navigator.userAgent.slice(0, 60)}`);
  logInfo('初始化', `后端桥接可用=${backendAvailable()}`);

  // ---- 第一步：立即绑定所有交互，保证任何情况下界面都可操作 ----
  bindEvents();
  logInfo('初始化', '事件绑定完成');

  // ---- 第二步：先切到色卡库，让界面立刻可用 ----
  switchView('library');

  // ---- 第三步：逐项加载资源，互不影响 ----
  await step('加载内置色卡', async () => {
    const res = await fetch('data/default_palettes.json', { cache: 'no-cache' });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    if (!data || !data.palettes) throw new Error('色卡文件结构异常');
    state.builtin = data;
    applyHueBins(data.hueBins);
    state.paletteKey = Object.keys(data.palettes)[0];
    const total = Object.values(data.palettes)
      .reduce((s, p) => s + (p.colors?.length || 0), 0);
    logInfo('色卡', `载入 ${Object.keys(data.palettes).length} 套 / ${total} 色`);
    renderPaletteTabs();
    renderHueChips();
    renderGrid();
  }, { required: true });

  await step('加载印刷标准', async () => {
    state.standards = await rpc('getStandards');
    $('standardSelect').innerHTML = state.standards.map((s) =>
      `<option value="${esc(s.id)}">${esc(s.name)}（${s.tacLimit}%）</option>`).join('');
    state.standardId = state.standards[0]?.id || 'pso-coated-v3';
    $('standardSelect').value = state.standardId;
    const s0 = state.standards[0];
    if (s0) $('tacNote').innerHTML =
      `<b>${esc(s0.name)}</b><br>总墨量上限 ${s0.tacLimit}% · 容差 ΔE ${s0.tolerableDeltaE}`;
    renderGrid();
  });

  await step('读取数据目录', async () => {
    const dd = await rpc('getDataDir');
    $('dataDir').textContent = dd.dir;
  });

  await step('加载我的预设', async () => {
    await loadPresets();
  });

  await step('读取系统日志', async () => {
    await loadServerLogs();
  });

  // 若在浏览器中直接打开（无后端），给出明确提示而非让人以为界面坏了
  if (!backendAvailable()) {
    $('dataDir').textContent = '未连接桌面后端';
    logWarn('初始化', '当前不在桌面应用环境中，色卡浏览与搜索可用，取色/预设功能不可用');
    $('libraryEmpty').hidden = true;
  }

  logInfo('初始化', '全部完成');
}

/** 绑定全部交互事件。必须最先执行，否则界面会「点击没反应」。 */
function bindEvents() {
  document.querySelectorAll('.nav-item').forEach((b) => {
    b.onclick = () => switchView(b.dataset.view);
  });

  let timer;
  $('searchInput').oninput = (e) => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      state.query = e.target.value;
      if (state.query) logInfo('搜索', `关键词「${state.query}」`);
      if (state.view !== 'library') switchView('library');
      else renderGrid();
    }, 130);
  };
  $('clearSearch').onclick = () => {
    $('searchInput').value = '';
    state.query = '';
    renderGrid();
  };

  $('refreshBtn').onclick = async () => {
    const btn = $('refreshBtn');
    const label = btn.textContent;
    btn.disabled = true;
    btn.textContent = '⟳ 抓取中…';
    try {
      const rep = await rpc('fetchPalettes');
      const failed = (rep.failed || []).filter((x) => !/已跳过/.test(x));
      const r = await fetch('data/default_palettes.json', { cache: 'reload' });
      state.builtin = await r.json();
      applyHueBins(state.builtin.hueBins);
      if (!state.builtin.palettes[state.paletteKey]) {
        state.paletteKey = Object.keys(state.builtin.palettes)[0];
      }
      renderPaletteTabs(); renderHueChips(); renderGrid();
      toast(failed.length
        ? `已更新 ${rep.colors} 色，${failed.length} 个源未取到`
        : `已更新 ${rep.palettes} 套 / ${rep.colors} 色`);
    } catch (e) {
      logError('色卡', '抓取失败', e.message);
      toast('抓取失败：' + e.message);
    } finally {
      btn.disabled = false;
      btn.textContent = label;
    }
  };

  $('standardSelect').onchange = (e) => selectStandard(e.target.value);
  $('colorCount').oninput = (e) => { $('colorCountOut').textContent = e.target.value; };
  $('pickFileBtn').onclick = () => pickFile('image');
  $('saveExtractBtn').onclick = saveExtractAsPreset;
  $('copyAllBtn').onclick = copyAllCmyk;
  $('importBtn').onclick = () => pickFile('card');
  $('dropzone').ondrop = (e) => { e.preventDefault(); onDropFile(e); };
  $('exportBtn').onclick = exportAll;
  $('exportViewBtn').onclick = exportCurrentView;
  $('openDirBtn').onclick = async () => {
    const r = await rpc('openDataDir');
    toast(r.opened ? '已打开数据目录' : ('无法打开：' + (r.error || '未知原因')));
  };

  $('logRefreshBtn').onclick = loadServerLogs;
  $('traceBtn').onclick = () => {
    const pn = $('tracePanel');
    pn.hidden = !pn.hidden;
    if (!pn.hidden) loadTrace();
  };
  $('traceRefreshBtn').onclick = loadTrace;
  $('traceExportBtn').onclick = exportTrace;
  $('logOpenBtn').onclick = async () => {
    try {
      const r = await rpc('openLogDir');
      toast(r.opened ? '已打开日志目录' : ('无法打开：' + (r.error || '未知原因')));
    } catch (e) { toast('无法打开日志目录：' + e.message); }
  };
  $('logExportBtn').onclick = exportLogs;
  $('logFilter').onchange = renderLogPanel;

  $('drawerClose').onclick = () => { $('drawer').hidden = true; };
  $('drawerMask').onclick = () => { $('drawer').hidden = true; };

  const dz = $('dropzone');
  dz.onclick = () => pickFile('image');
  dz.ondragover = (e) => { e.preventDefault(); dz.classList.add('drag'); };
  dz.ondragleave = () => dz.classList.remove('drag');
  dz.ondrop = onDropFile;

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !$('drawer').hidden) $('drawer').hidden = true;
    if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
      e.preventDefault();
      $('searchInput').focus();
      $('searchInput').select();
    }
  });
}

/**
 * 拖放处理。
 * WebView2 出于安全默认**不提供 File.path**，拖进来的文件只有文件名。
 * 因此不能靠路径，必须把文件内容读成 base64 交给后端。
 */
async function onDropFile(e) {
  e.preventDefault();
  const dz = $('dropzone');
  dz.classList.remove('drag');
  const f = e.dataTransfer.files[0];
  if (!f) return;
  if (PICK.busy) {
    logWarn('取色', '上一张图片仍在解析中，请稍候');
    return;
  }
  PICK.busy = true;

  logInfo('取色', `拖入文件 ${f.name}（${f.size} 字节）`);
  const ext = (f.name.split('.').pop() || '').toLowerCase();
  const isPaletteFile = ['ase', 'aseu', 'gpl', 'css', 'json', 'hex', 'txt'].includes(ext);

  // 优先用真实路径（部分环境可能可用）
  const path = f.path || '';
  if (path) {
    logInfo('取色', '该环境提供了完整路径，直接使用');
    if (isPaletteFile) return importFile(path);
    return extractFrom(path);
  }

  // 常规路径：读取内容上传
  try {
    if (isPaletteFile) {
      // 文本类色卡在前端解析；二进制（ASE）交给后端
      if (ext === 'ase' || ext === 'aseu') {
        const b64 = await readAsBase64(f);
        return await rpc('importFileData', {
          fileName: f.name, dataBase64: b64, isBase64: true,
        }).then(() => loadPresets()).then(() => {
          toast(`已导入 ${f.name}`);
          switchView('presets');
        });
      }
      const text = await f.text();
      const colors = [...text.matchAll(/([A-Za-z0-9 _\-\u4e00-\u9fa5]+)?\s*#([0-9a-fA-F]{6})/g)]
        .map((m) => ({ name: (m[1] || '').trim(), hex: '#' + m[2].toLowerCase(), hueBin: '', family: '' }));
      if (!colors.length) return toast('未能从该文件解析出颜色');
      await rpc('savePalette', {
        name: `${f.name}（拖入导入）`, colors,
        standardId: state.standardId, source: 'import',
      });
      await loadPresets();
      toast(`已导入 ${colors.length} 色`);
      switchView('presets');
    } else {
      // 图片：转 base64 上传，由后端解码后取色
      const b64 = await readAsBase64(f);
      const r = await rpc('extractData', {
        fileName: f.name, dataBase64: b64, isBase64: true,
        maxColors: +$('colorCount').value, standardId: state.standardId,
      });
      state.extract = r;
      renderExtract(r);
      switchView('picker');
      toast(`已从 ${f.name} 提取 ${r.colors.length} 色`);
    }
  } catch (err) {
    logError('取色', `拖入处理失败：${f.name}`, err.message);
    setPickStatus('error', '处理失败：' + err.message);
    toast('处理失败：' + err.message);
  } finally {
    PICK.busy = false;
  }
}

/** 读取文件为 base64（去掉 data URL 前缀）。 */
function readAsBase64(file) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => {
      const s = String(r.result || '');
      const i = s.indexOf(',');
      resolve(i >= 0 ? s.slice(i + 1) : s);
    };
    r.onerror = () => reject(new Error('读取文件失败'));
    r.readAsDataURL(file);
  });
}

/** 加载并渲染端到端追踪（后端 Trace + 前端 FE_TRACE 合并按时间排序）。 */
async function loadTrace() {
  const box = $('traceList');
  if (!box) return;
  box.innerHTML = '<div class="empty"><p>读取中…</p></div>';
  let be = [];
  try {
    const r = await rpc('getTrace');
    be = r.entries || [];
    $('logDir').textContent = `追踪文件: ${r.file || ''}`;
  } catch (e) {
    box.innerHTML = `<div class="empty"><p>读取后端追踪失败</p><span>${esc(e.message)}</span></div>`;
    return;
  }
  const rows = [
    ...be.map((e) => ({
      seq: '#' + String(e.seq).padStart(4, '0'), time: e.time, dir: e.dir,
      ch: e.channel, sum: e.summary, det: e.detail, bytes: e.bytes, side: 'BE',
    })),
    ...FE_TRACE.map((e) => ({
      seq: 'FE', time: e.time, dir: e.dir, ch: e.channel,
      sum: e.summary, det: e.detail, bytes: e.bytes, side: 'FE',
    })),
  ].sort((a, b) => (b.time || '').localeCompare(a.time || ''));
  if (!rows.length) {
    box.innerHTML = '<div class="empty"><p>暂无追踪记录</p><span>操作一次即可产生</span></div>';
    return;
  }
  box.innerHTML = rows.map((r) => `
    <div class="trace-line ${esc(r.dir)}">
      <span class="trace-seq">${esc(r.seq)}</span>
      <span class="trace-time">${esc(r.time)}</span>
      <span class="trace-dir">${esc(r.dir)}</span>
      <span class="trace-ch" title="${esc(r.ch)}">${esc(r.side)}·${esc(r.ch)}${r.bytes ? ' ' + r.bytes + 'B' : ''}</span>
      <span class="trace-sum">${esc(r.sum)}</span>
      ${r.det ? `<div class="trace-det">${esc(r.det)}</div>` : ''}
    </div>`).join('');
}

async function exportTrace() {
  let text = '=== 前端追踪 ===\n';
  FE_TRACE.forEach((e, i) => {
    text += `#${String(i + 1).padStart(4, '0')} [FE/${e.dir}] ${e.channel} ${e.bytes}B\n` +
            `         ${e.time} ${e.summary}\n         ${e.detail}\n`;
  });
  try {
    const r = await rpc('exportTrace');
    text += '\n\n' + r.text;
  } catch (e) {
    text += `\n（后端追踪读取失败：${e.message}）`;
  }
  const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
  const path = await saveViaDialog(`colormod-trace-${stamp}.txt`, text, '文本文件|*.txt|所有文件|*.*');
  if (path) toast('追踪已导出');
}

/** 读取后端日志并合并到前端面板。 */
async function loadServerLogs() {
  if (!backendAvailable()) {
    renderLogPanel();
    return;
  }
  try {
    const level = $('logFilter')?.value || 'Debug';
    const r = await rpc('getLogs', { maxColors: 300, minLevel: level });
    $('logDir').textContent = r.dir || '';
    // 后端日志在前端基础上补充，按时间倒序去重展示
    const seen = new Set(LOG_BUFFER.map((e) => `${e.time}|${e.message}`));
    for (const e of (r.entries || []).slice().reverse()) {
      const key = `${e.time}|${e.message}`;
      if (seen.has(key)) continue;
      LOG_BUFFER.push({
        time: e.time, level: e.level.toLowerCase(),
        scope: e.category, message: e.message, detail: e.detail || '',
      });
    }
    if (LOG_BUFFER.length > LOG_MAX) LOG_BUFFER.splice(0, LOG_BUFFER.length - LOG_MAX);
    renderLogPanel();
  } catch (e) {
    logError('日志', '读取后端日志失败', e.message);
  }
}

async function exportLogs() {
  const text = LOG_BUFFER.slice().reverse().map((e) =>
    `[${e.time}] [${e.level.toUpperCase()}] [${e.scope}] ${e.message}` +
    (e.detail ? `\n    ${e.detail}` : '')).join('\n');
  const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
  const path = await saveViaDialog(`colormod-log-${stamp}.txt`, text, '文本文件|*.txt|所有文件|*.*');
  if (path) toast('日志已导出');
}

document.addEventListener('DOMContentLoaded', init);

