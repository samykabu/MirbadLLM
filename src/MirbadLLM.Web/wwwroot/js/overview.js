// The Overview's browser side, loaded as an ES module through Blazor JS interop (no library, no CDN):
//  - rail(): the node scope rail's phone-style page dots (one per screenful, the current one follows the scroll, a click
//    jumps); all of it runs in the browser, so scrolling never costs a server round trip.
//  - signal(): the Signal chart, plain WebGL1 with a Canvas2D fallback. The page pushes the window's series on every
//    refresh (never per frame); colours come from the --ss-* custom properties, so both themes work.

const reduceMotion = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

// ---------------------------------------------------------------- node rail page dots

export function rail(track, dots) {
    if (!track || !dots) {
        return { dispose() { } };
    }

    let count = 0;
    let current = -1;

    function pages() {
        return Math.max(1, Math.ceil(track.scrollWidth / Math.max(1, track.clientWidth) - 0.01));
    }

    function page() {
        const max = track.scrollWidth - track.clientWidth;
        if (max <= 0) { return 0; }
        // The last page is wherever the scroll ends, even when it is not a whole screenful.
        return track.scrollLeft >= max - 2 ? count - 1 : Math.round(track.scrollLeft / Math.max(1, track.clientWidth));
    }

    function build() {
        const n = pages();
        if (n === count) { return; }
        count = n;
        current = -1;
        dots.replaceChildren();
        dots.hidden = n < 2;
        for (let i = 0; i < n; i++) {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'scope-dot';
            b.setAttribute('aria-label', 'Nodes, page ' + (i + 1) + ' of ' + n);
            b.appendChild(document.createElement('span'));
            b.addEventListener('click', () => {
                track.scrollTo({ left: i * track.clientWidth, behavior: reduceMotion() ? 'auto' : 'smooth' });
            });
            dots.appendChild(b);
        }
    }

    function sync() {
        build();
        const p = Math.min(page(), count - 1);
        if (p === current) { return; }
        current = p;
        Array.prototype.forEach.call(dots.children, (b, i) => {
            b.classList.toggle('is-current', i === p);
            if (i === p) { b.setAttribute('aria-current', 'true'); } else { b.removeAttribute('aria-current'); }
        });
    }

    let frame = 0;
    const onScroll = () => {
        if (!frame) { frame = requestAnimationFrame(() => { frame = 0; sync(); }); }
    };
    track.addEventListener('scroll', onScroll, { passive: true });
    const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(onScroll) : null;
    if (resize) { resize.observe(track); }
    // Cards come and go with the fleet; Blazor re-renders them inside the track.
    const mutations = new MutationObserver(onScroll);
    mutations.observe(track, { childList: true });
    sync();

    return {
        // A selected card scrolls into view (a deep link to ?node= on a long rail).
        reveal() {
            const pressed = track.querySelector('[aria-pressed="true"]');
            if (pressed && pressed.offsetLeft + pressed.offsetWidth > track.scrollLeft + track.clientWidth) {
                track.scrollLeft = pressed.offsetLeft - 8;
            }
            sync();
        },
        dispose() {
            track.removeEventListener('scroll', onScroll);
            if (resize) { resize.disconnect(); }
            mutations.disconnect();
            if (frame) { cancelAnimationFrame(frame); }
        }
    };
}

// ---------------------------------------------------------------- Signal chart

// The page sends the layout (data.area: the stacked area's share of the height from the top, data.gap: the space before
// the heat strip), so the HTML labels and both renderers agree.
const MAX_MODELS = 16;
const MAX_ROWS = 12;

let probe = null;

function css(el, name, fallback) {
    const v = getComputedStyle(el).getPropertyValue(name).trim();
    return v || fallback;
}

// "var(--ss-cat-2)" or "#2ab7a9" → [r, g, b] in 0–1, through the browser's own colour parser.
function rgb(el, value) {
    const m = /^var\((--[\w-]+)\)$/.exec(value || '');
    const resolved = m ? css(el, m[1], '#888888') : (value || '#888888');
    probe = probe || document.createElement('canvas').getContext('2d');
    if (!probe) { return [0.5, 0.5, 0.5]; }
    probe.fillStyle = '#000000';
    probe.fillStyle = resolved;
    const hex = probe.fillStyle; // normalised to #rrggbb (or rgba(...) with alpha)
    if (hex[0] === '#') {
        return [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
    }
    const parts = hex.match(/[\d.]+/g) || ['128', '128', '128'];
    return parts.slice(0, 3).map((x) => Number(x) / 255);
}

function palette(el, data) {
    return {
        bg: rgb(el, 'var(--ss-panel)'),
        grid: rgb(el, 'var(--ss-border)'),
        well: rgb(el, 'var(--ss-sunken)'),
        heat: rgb(el, 'var(--ss-accent)'),
        hot: rgb(el, 'var(--ss-busy)'),
        models: (data.models || []).slice(0, MAX_MODELS).map((m) => rgb(el, m.color)),
    };
}

// Per column: the stacked tops of each model (0–1 of the chart's max) and each heat row (0–1, or -1 for no reading).
function pack(data) {
    const n = Math.max(2, data.columns | 0);
    const models = (data.models || []).slice(0, MAX_MODELS);
    const rows = (data.heat || []).slice(0, MAX_ROWS);
    const max = data.max > 0 ? data.max : 1;
    const tops = models.map(() => new Float32Array(n));
    const known = new Uint8Array(n);
    for (let c = 0; c < n; c++) {
        let sum = 0;
        for (let i = 0; i < models.length; i++) {
            const v = models[i].values[c];
            if (v !== null && v !== undefined && isFinite(v)) { sum += Math.max(0, v); known[c] = 1; }
            tops[i][c] = Math.min(1, sum / max);
        }
    }
    const heat = rows.map((r) => {
        const out = new Float32Array(n);
        for (let c = 0; c < n; c++) {
            const v = r.values[c];
            out[c] = v === null || v === undefined || !isFinite(v) ? -1 : Math.max(0, Math.min(1, v / 100));
        }
        return out;
    });
    const area = data.area > 0 && data.area <= 1 ? data.area : 1;
    const gap = data.gap >= 0 && data.gap < 1 ? data.gap : 0;
    return { n, tops, known, heat, area, gap };
}

const VS = 'attribute vec2 p;void main(){gl_Position=vec4(p,0.,1.);}';
// Texture: one row per model (its stacked top in R, A = 255 where any model has a reading), then one per heat row
// (value in R, A = 0 for no reading). Uniform colours come from the CSS tokens.
const FS = [
    'precision mediump float;',
    'uniform sampler2D D;uniform vec2 R;uniform float M;uniform float H;uniform float ROWS;uniform float REVEAL;uniform float AREA;uniform float GAP;',
    'uniform vec3 BG;uniform vec3 GR;uniform vec3 WELL;uniform vec3 HEAT;uniform vec3 HOT;uniform vec3 C[' + MAX_MODELS + '];',
    'float tex(float col,float row){return texture2D(D,vec2(col,(row+0.5)/ROWS)).r;}',
    'float has(float col,float row){return texture2D(D,vec2(col,(row+0.5)/ROWS)).a;}',
    'void main(){',
    ' vec2 uv=gl_FragCoord.xy/R;vec3 col=BG;',
    ' if(uv.x>REVEAL){gl_FragColor=vec4(BG,1.);return;}',
    ' float top=1.-AREA;',
    ' if(uv.y>=top){',
    '  float y=(uv.y-top)/AREA;',
    '  float gy=abs(fract(y*4.)-0.5);col=mix(col,GR,smoothstep(0.47,0.5,gy)*0.9);',
    '  if(M>0.&&has(uv.x,0.)>0.5){',
    '   float below=0.;',
    '   for(int i=0;i<' + MAX_MODELS + ';i++){',
    '    if(float(i)>=M)break;',
    '    float t=tex(uv.x,float(i));',
    '    if(y<=t&&y>=below){float k=(y-below)/max(t-below,0.0001);col=mix(BG,C[i],0.30+0.45*k);',
    '     float edge=1.-smoothstep(0.,2.5/(R.y*AREA),t-y);col=mix(col,C[i],edge);}',
    '    below=t;',
    '   }',
    '  }',
    ' } else if(H>0.&&uv.y<top-GAP){',
    '  float band=(top-GAP)/H;float r=floor(uv.y/band);float ly=fract(uv.y/band);',
    '  if(ly>0.12&&ly<0.88){',
    '   float row=M+(H-1.-r);',
    '   float v=tex(uv.x,row);float k=has(uv.x,row);',
    '   vec3 c=mix(WELL,HEAT,smoothstep(0.02,0.75,v));c=mix(c,HOT,smoothstep(0.8,1.,v));',
    '   col=k>0.5?c:WELL;',
    '  }',
    ' }',
    ' gl_FragColor=vec4(col,1.);',
    '}'
].join('\n');

function startGl(canvas) {
    let gl = null;
    try {
        gl = canvas.getContext('webgl', { antialias: true, preserveDrawingBuffer: false }) || canvas.getContext('experimental-webgl');
    } catch (e) {
        gl = null;
    }
    if (!gl) { return null; }
    const shader = (type, src) => {
        const s = gl.createShader(type);
        gl.shaderSource(s, src);
        gl.compileShader(s);
        return gl.getShaderParameter(s, gl.COMPILE_STATUS) ? s : null;
    };
    const vs = shader(gl.VERTEX_SHADER, VS);
    const fs = shader(gl.FRAGMENT_SHADER, FS);
    if (!vs || !fs) { return null; }
    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) { return null; }
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(program, 'p');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    const u = {};
    ['D', 'R', 'M', 'H', 'ROWS', 'REVEAL', 'AREA', 'GAP', 'BG', 'GR', 'WELL', 'HEAT', 'HOT', 'C'].forEach((n) => { u[n] = gl.getUniformLocation(program, n); });
    return { gl, u, texture };
}

function drawGl(state, packed, colors, reveal) {
    const { gl, u } = state;
    const canvas = gl.canvas;
    const m = packed.tops.length;
    const h = packed.heat.length;
    const rows = Math.max(1, m + h);
    const n = packed.n;
    const bytes = new Uint8Array(n * rows * 4);
    for (let i = 0; i < m; i++) {
        for (let c = 0; c < n; c++) {
            const o = (i * n + c) * 4;
            bytes[o] = Math.round(packed.tops[i][c] * 255);
            bytes[o + 3] = packed.known[c] ? 255 : 0;
        }
    }
    for (let r = 0; r < h; r++) {
        for (let c = 0; c < n; c++) {
            const o = ((m + r) * n + c) * 4;
            const v = packed.heat[r][c];
            bytes[o] = v < 0 ? 0 : Math.round(v * 255);
            bytes[o + 3] = v < 0 ? 0 : 255;
        }
    }
    gl.bindTexture(gl.TEXTURE_2D, state.texture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, n, rows, 0, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
    gl.viewport(0, 0, canvas.width, canvas.height);
    gl.uniform1i(u.D, 0);
    gl.uniform2f(u.R, canvas.width, canvas.height);
    gl.uniform1f(u.M, m);
    gl.uniform1f(u.H, h);
    gl.uniform1f(u.ROWS, rows);
    gl.uniform1f(u.REVEAL, reveal);
    gl.uniform1f(u.AREA, packed.area);
    gl.uniform1f(u.GAP, packed.gap);
    gl.uniform3fv(u.BG, colors.bg);
    gl.uniform3fv(u.GR, colors.grid);
    gl.uniform3fv(u.WELL, colors.well);
    gl.uniform3fv(u.HEAT, colors.heat);
    gl.uniform3fv(u.HOT, colors.hot);
    const flat = new Float32Array(MAX_MODELS * 3);
    colors.models.forEach((c, i) => { flat.set(c, i * 3); });
    gl.uniform3fv(u.C, flat);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
}

// The same chart on a 2D canvas, for browsers or GPUs without WebGL.
function draw2d(ctx, packed, colors, reveal) {
    const canvas = ctx.canvas;
    const w = canvas.width;
    const hgt = canvas.height;
    const css3 = (c, a) => 'rgba(' + Math.round(c[0] * 255) + ',' + Math.round(c[1] * 255) + ',' + Math.round(c[2] * 255) + ',' + (a === undefined ? 1 : a) + ')';
    ctx.clearRect(0, 0, w, hgt);
    ctx.fillStyle = css3(colors.bg);
    ctx.fillRect(0, 0, w, hgt);
    const area = hgt * packed.area;
    ctx.strokeStyle = css3(colors.grid);
    ctx.lineWidth = 1;
    for (let g = 1; g < 4; g++) {
        const y = Math.round(area * g / 4) + 0.5;
        ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
    }
    const n = packed.n;
    const x = (c) => (c + 0.5) / n * w;
    const yOf = (t) => area - t * area;
    ctx.save();
    ctx.beginPath(); ctx.rect(0, 0, w * reveal, hgt); ctx.clip();
    for (let i = packed.tops.length - 1; i >= 0; i--) {
        const below = i > 0 ? packed.tops[i - 1] : null;
        ctx.beginPath();
        let open = false;
        for (let c = 0; c < n; c++) {
            if (!packed.known[c]) { continue; }
            if (!open) { ctx.moveTo(x(c), yOf(packed.tops[i][c])); open = true; } else { ctx.lineTo(x(c), yOf(packed.tops[i][c])); }
        }
        for (let c = n - 1; c >= 0; c--) {
            if (!packed.known[c]) { continue; }
            ctx.lineTo(x(c), yOf(below ? below[c] : 0));
        }
        ctx.closePath();
        ctx.fillStyle = css3(colors.models[i], 0.55);
        ctx.fill();
        ctx.strokeStyle = css3(colors.models[i]);
        ctx.lineWidth = 2;
        ctx.beginPath();
        open = false;
        for (let c = 0; c < n; c++) {
            if (!packed.known[c]) { open = false; continue; }
            if (!open) { ctx.moveTo(x(c), yOf(packed.tops[i][c])); open = true; } else { ctx.lineTo(x(c), yOf(packed.tops[i][c])); }
        }
        ctx.stroke();
    }
    const top = hgt * (packed.area + packed.gap);
    const rows = packed.heat.length;
    const band = rows > 0 ? (hgt - top) / rows : 0;
    for (let r = 0; r < rows; r++) {
        for (let c = 0; c < n; c++) {
            const v = packed.heat[r][c];
            const mix = (a, b, k) => [0, 1, 2].map((j) => a[j] + (b[j] - a[j]) * k);
            let col = v < 0 ? colors.well : mix(colors.well, colors.heat, Math.min(1, Math.max(0, (v - 0.02) / 0.73)));
            if (v > 0.8) { col = mix(col, colors.hot, (v - 0.8) / 0.2); }
            ctx.fillStyle = css3(col);
            ctx.fillRect(c / n * w, top + r * band + band * 0.12, Math.ceil(w / n), band * 0.76);
        }
    }
    ctx.restore();
}

export function signal(canvas) {
    let target = canvas;
    let gl = null;
    let ctx2d = null;
    let data = null;
    let colors = null;
    let reveal = 1;
    let animating = 0;
    let drawnOnce = false;

    function size(c) {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const w = Math.max(1, Math.round(c.clientWidth * dpr));
        const h = Math.max(1, Math.round(c.clientHeight * dpr));
        if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
    }

    function onLost(e) { e.preventDefault(); gl = null; }
    function onRestored() { attach(target); render(); }

    function attach(c) {
        target = c;
        gl = startGl(c);
        ctx2d = gl ? null : c.getContext('2d');
        c.dataset.renderer = gl ? 'webgl' : (ctx2d ? 'canvas2d' : 'none');
        c.addEventListener('webglcontextlost', onLost);
        c.addEventListener('webglcontextrestored', onRestored);
    }

    function release() {
        if (!target) { return; }
        target.removeEventListener('webglcontextlost', onLost);
        target.removeEventListener('webglcontextrestored', onRestored);
        if (gl) {
            const ext = gl.gl.getExtension('WEBGL_lose_context');
            if (ext) { ext.loseContext(); }
        }
        gl = null;
        ctx2d = null;
    }

    function render() {
        if (!data || !target || !target.isConnected) { return; }
        size(target);
        colors = palette(target, data);
        const packed = pack(data);
        if (gl) {
            drawGl(gl, packed, colors, reveal);
        } else if (ctx2d) {
            draw2d(ctx2d, packed, colors, reveal);
        }
    }

    // Lines draw in once, left to right (DESIGN.md motion); a static frame under reduced motion.
    function drawIn() {
        if (reduceMotion()) { reveal = 1; render(); return; }
        const t0 = performance.now();
        const step = (now) => {
            reveal = Math.min(1, (now - t0) / 600);
            render();
            animating = reveal < 1 ? requestAnimationFrame(step) : 0;
        };
        animating = requestAnimationFrame(step);
    }

    const resize = typeof ResizeObserver === 'function' ? new ResizeObserver(() => render()) : null;
    // The theme lives on <html data-bs-theme>; the OS scheme can change it too.
    const themeWatch = new MutationObserver(() => render());
    themeWatch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme', 'data-theme', 'class'] });
    const scheme = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    if (scheme && scheme.addEventListener) { scheme.addEventListener('change', render); }

    attach(canvas);
    if (resize) { resize.observe(canvas); }

    return {
        update(c, next) {
            // Blazor can hand over a new <canvas> after a re-render: start again on it.
            if (c && c !== target) {
                if (resize) { resize.unobserve(target); }
                release();
                attach(c);
                if (resize) { resize.observe(c); }
            }
            data = next;
            if (!drawnOnce && data && (data.models || []).length + (data.heat || []).length > 0) {
                drawnOnce = true;
                reveal = 0;
                drawIn();
                return;
            }
            render();
        },
        dispose() {
            if (animating) { cancelAnimationFrame(animating); }
            if (resize) { resize.disconnect(); }
            themeWatch.disconnect();
            if (scheme && scheme.removeEventListener) { scheme.removeEventListener('change', render); }
            release();
            target = null;
        }
    };
}
