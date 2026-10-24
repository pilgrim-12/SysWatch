import { useEffect, useRef, useState } from 'react';
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { CSS2DObject, CSS2DRenderer } from 'three/examples/jsm/renderers/CSS2DRenderer.js';
import { bytes, dayTime } from '../format';
import { t } from '../i18n';
import { describePath } from '../pathLabel';
import { HUES, squarify, type MapItem } from './Treemap';
import { countRest, countUnder, under } from '../marks';
import { api, type FolderEntry } from '../api';

/**
 * «Город папок»: каждая папка — квартал, её подпапки и файлы — здания на нём.
 * Площадь — размер, высота — тоже размер (по корню, чтобы мелкое не исчезало). Выросшие папки подсвечены.
 * Вращение — мышью, колесо — приближение, клик — зайти в папку.
 */

const W = 100, D = 62;          // размер «земли» в условных единицах
const GAP = 0.9;                // улицы между кварталами
const MAX_H = 20;               // высота самого большого здания

type Hit = { path: string | null; title: string; lines: string[]; open: boolean; file?: string };

/** Цвет файла по типу: файлы — светящиеся шестигранные кристаллы, папки — здания. */
export const FILE_TYPES: { label: () => string; color: number; ext: string[] }[] = [
  { label: () => t('Видео'), color: 0xb46cf0, ext: ['mp4', 'mkv', 'avi', 'mov', 'wmv', 'webm', 'm4v', 'ts'] },
  { label: () => t('Фото'), color: 0x5fd18a, ext: ['jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp', 'heic', 'tif', 'tiff', 'raw', 'svg'] },
  { label: () => t('Документы'), color: 0x5aa8f0, ext: ['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'txt', 'rtf', 'csv', 'md', 'odt'] },
  { label: () => t('Архивы'), color: 0xf0a640, ext: ['zip', 'rar', '7z', 'tar', 'gz', 'iso', 'img', 'cab', 'vhd', 'vhdx'] },
  { label: () => t('Программы'), color: 0xf06a5a, ext: ['exe', 'msi', 'dll', 'sys', 'msix', 'appx'] },
  { label: () => t('Музыка'), color: 0xf0d860, ext: ['mp3', 'wav', 'flac', 'm4a', 'aac', 'ogg'] },
];
const OTHER_FILE = 0x9fb3c8;
const fileColor = (name: string) => {
  const ext = name.slice(name.lastIndexOf('.') + 1).toLowerCase();
  return FILE_TYPES.find(f => f.ext.includes(ext))?.color ?? OTHER_FILE;
};

/** Сколько мелких файлов папки ставить отдельными зданиями. */
const MAX_FILES = 150;

export default function Disk3D({ items, other, total, onOpen, marks = [], base: basePath = '', files = [] }: {
  items: MapItem[]; other: number; total: number; onOpen: (path: string) => void;
  /** Что лежит в папке прямо сейчас: мелкие файлы встают отдельными зданиями в квартале «Остальное». */
  files?: readonly FolderEntry[];
  /** Найденные файлы: над зданиями, где они лежат, — световой маяк. */
  marks?: readonly string[]; base?: string;
}) {
  const host = useRef<HTMLDivElement>(null);
  const [tip, setTip] = useState<(Hit & { x: number; y: number }) | null>(null);
  const [webgl, setWebgl] = useState(true);
  const onOpenRef = useRef(onOpen);
  onOpenRef.current = onOpen;

  useEffect(() => {
    const el = host.current!;
    let renderer: THREE.WebGLRenderer;
    try { renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false }); }
    catch { setWebgl(false); return; }

    const width = () => el.clientWidth, height = () => el.clientHeight;
    renderer.setPixelRatio(Math.min(2, window.devicePixelRatio));
    renderer.setSize(width(), height());
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.setClearColor(0x0d1726);
    el.appendChild(renderer.domElement);

    const labels = new CSS2DRenderer();
    labels.setSize(width(), height());
    labels.domElement.className = 'd3-labels';
    el.appendChild(labels.domElement);

    const scene = new THREE.Scene();
    scene.fog = new THREE.Fog(0x0d1726, 110, 230);

    const camera = new THREE.PerspectiveCamera(42, width() / height(), 0.5, 600);
    camera.position.set(46, 52, 70);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.target.set(0, 4, 0);
    controls.enableDamping = true;
    controls.dampingFactor = 0.08;
    controls.maxPolarAngle = Math.PI * 0.47; // не уходить под землю
    controls.minDistance = 18;
    controls.maxDistance = 220;
    controls.autoRotate = true;
    controls.autoRotateSpeed = 0.6;
    const stopAuto = () => { controls.autoRotate = false; };
    renderer.domElement.addEventListener('pointerdown', stopAuto);

    // Свет: мягкий общий + «солнце» с тенями.
    scene.add(new THREE.HemisphereLight(0xbfd7ff, 0x0d1726, 0.85));
    const sun = new THREE.DirectionalLight(0xffffff, 1.25);
    sun.position.set(-50, 90, 40);
    sun.castShadow = true;
    sun.shadow.mapSize.set(2048, 2048);
    Object.assign(sun.shadow.camera, { left: -80, right: 80, top: 80, bottom: -80, near: 1, far: 260 });
    scene.add(sun);

    // Земля и сетка.
    const ground = new THREE.Mesh(new THREE.PlaneGeometry(W * 2.4, D * 2.8), new THREE.MeshStandardMaterial({ color: 0x111e31, roughness: 1 }));
    ground.rotation.x = -Math.PI / 2;
    ground.receiveShadow = true;
    scene.add(ground);
    const grid = new THREE.GridHelper(260, 52, 0x1f3250, 0x16243a);
    (grid.material as THREE.Material).transparent = true;
    (grid.material as THREE.Material).opacity = 0.5;
    grid.position.y = 0.01;
    scene.add(grid);

    // ── Город ──
    const city = new THREE.Group();
    scene.add(city);
    const pickable: THREE.Mesh[] = [];
    const growing: { mesh: THREE.Mesh; h: number }[] = [];
    const disposables: { dispose: () => void }[] = [];
    const beacons: { x: number; z: number; top: number; n: number }[] = [];
    const box = new THREE.BoxGeometry(1, 1, 1);
    box.translate(0, 0.5, 0); // основание на земле — удобно масштабировать по высоте
    disposables.push(box);

    const maxSize = Math.max(1, ...items.flatMap(i => i.children.length ? i.children.map(c => c.size) : [i.size]), other);
    const heightOf = (s: number) => Math.max(0.6, MAX_H * Math.cbrt(s / maxSize)); // кубический корень: большое не заслоняет мелкое
    const mat = (color: THREE.ColorRepresentation, opts: Partial<THREE.MeshStandardMaterialParameters> = {}) => {
      const m = new THREE.MeshStandardMaterial({ color, roughness: 0.55, metalness: 0.15, ...opts });
      disposables.push(m);
      return m;
    };
    // Файл — шестигранный кристалл, вписанный в свой участок: сразу отличается от прямоугольных папок.
    const prism = new THREE.CylinderGeometry(0.4, 0.5, 1, 6);
    prism.translate(0, 0.5, 0);
    disposables.push(prism);
    const place = (x: number, z: number, w: number, d: number, h: number, material: THREE.Material, hit: Hit, y = 0, geo: THREE.BufferGeometry = box) => {
      const mesh = new THREE.Mesh(geo, material);
      mesh.position.set(x + w / 2 - W / 2, y, z + d / 2 - D / 2);
      if (geo === prism) { const s = Math.max(0.08, Math.min(w, d) * 0.92); w = d = s; }
      mesh.scale.set(Math.max(0.05, w), 0.001, Math.max(0.05, d));
      mesh.castShadow = true;
      mesh.receiveShadow = true;
      mesh.userData = hit;
      city.add(mesh);
      pickable.push(mesh);
      growing.push({ mesh, h });
      return mesh;
    };

    const fileMat = (name: string, found: boolean) => {
      const c = fileColor(name);
      return mat(c, { emissive: found ? 0x1d8fa3 : c, emissiveIntensity: found ? 1.2 : 0.28, metalness: 0.35, roughness: 0.3, transparent: true, opacity: 0.92 });
    };

    // Квартал мелких файлов честно маленький по объёму, но получает не меньше 18% земли, иначе файлов не разглядеть.
    const hasFiles = files.some(f => !f.isDir && f.size > 0);
    const sumItems = items.reduce((a, i) => a + i.size, 0);
    const otherArea = hasFiles ? Math.max(other, (sumItems + other) * 0.18) : other;
    const values = [...items.map(i => i.size), otherArea];
    const rects = squarify(values, { x: 0, y: 0, w: W, h: D });
    let hue = 0;
    let labelCount = 0;
    const pct = (v: number) => (total > 0 ? `${((v / total) * 100).toFixed(1)}%` : '');

    rects.forEach((r, k) => {
      if (r.w < 0.3 || r.h < 0.3) return;
      const x = r.x + GAP / 2, z = r.y + GAP / 2, w = r.w - GAP, d = r.h - GAP;
      if (w <= 0 || d <= 0) return;
      const item = k < items.length ? items[k] : null;

      if (!item) {
        const rest = marks.length ? countRest(marks, basePath, items.map(i => i.path)) : 0;
        const known = new Set(items.map(i => i.path.toLowerCase()));
        const small = files.filter(f => !f.isDir && f.size > 0 && !known.has(f.path.toLowerCase()))
          .sort((a, b) => b.size - a.size).slice(0, MAX_FILES);
        if (small.length && w > 3 && d > 3) {
          // Квартал мелких файлов: платформа + здание на каждый файл, остаток — низкий блок.
          const pad = 0.6, h0 = 0.35;
          place(x, z, w, d, h0, mat(0x22324a), { path: null, title: t('Остальное'), lines: [bytes(other), t('мелкие файлы и папки до 100 МБ')], open: false });
          const sum = small.reduce((a, f) => a + f.size, 0);
          const restBytes = Math.max(0, other - sum);
          const fr = squarify([...small.map(f => f.size), Math.min(restBytes, sum * 0.35)], { x: x + pad, y: z + pad, w: w - pad * 2, h: d - pad * 2 });
          const maxSmall = small[0].size;
          const fh = (v: number) => Math.max(0.25, 7 * Math.cbrt(v / maxSmall));
          let marked = 0;
          fr.forEach((r, j) => {
            const g = 0.18;
            if (r.w - g < 0.08 || r.h - g < 0.08) return;
            const f = j < small.length ? small[j] : null;
            if (!f) {
              place(r.x + g / 2, r.y + g / 2, r.w - g, r.h - g, 0.3, mat(0x2a3a52), { path: null, title: t('Остальное'), lines: [bytes(restBytes), t('мелкие папки и прочие файлы')], open: false }, h0);
              return;
            }
            const hitMark = marks.some(m => under(m, f.path));
            if (hitMark) { marked++; beacons.push({ x: r.x + r.w / 2, z: r.y + r.h / 2, top: h0 + fh(f.size), n: 1 }); }
            place(r.x + g / 2, r.y + g / 2, r.w - g, r.h - g, fh(f.size), fileMat(f.name, hitMark),
              { path: null, file: f.path, title: f.name, lines: [bytes(f.size), ...(f.modified ? [dayTime(f.modified)] : []), t('Нажми — показать в проводнике')], open: true }, h0, prism);
          });
          if (rest > marked) beacons.push({ x: x + w / 2, z: z + d / 2, top: h0, n: rest - marked });
          return;
        }
        place(x, z, w, d, 0.8, mat(0x2a3a52, rest ? { emissive: 0x1d6b78, emissiveIntensity: 0.9 } : {}), { path: null, title: t('Остальное'), lines: [bytes(other), t('мелкие файлы и папки до 100 МБ'), ...(rest ? [t('Найдено файлов: {0}', rest)] : [])], open: false });
        if (rest) beacons.push({ x: x + w / 2, z: z + d / 2, top: 0.8, n: rest });
        return;
      }

      const color = new THREE.Color(item.isDir ? HUES[hue++ % HUES.length] : '#4a5a70');
      const grew = (item.delta ?? 0) > 300 * 1024 * 1024;
      const info = describePath(item.path);
      const baseLines = [bytes(item.size) + (pct(item.size) ? ` · ${pct(item.size)}` : ''),
        ...(item.delta ? [`${item.delta > 0 ? '+' : '−'}${bytes(Math.abs(item.delta))}`] : []),
        ...(info ? [info.label] : []),
        ...(item.isDir ? [t('Нажми, чтобы открыть')] : [])];
      const found = marks.length ? countUnder(marks, item.path) : 0;
      if (found) baseLines.push(t('Найдено файлов: {0}', found));
      const hit: Hit = { path: item.isDir ? item.path : null, title: item.name, lines: baseLines, open: item.isDir };
      const glow = { emissive: 0x1d8fa3, emissiveIntensity: 1.1 };

      if (!item.isDir) {
        place(x, z, w, d, heightOf(item.size), fileMat(item.name, !!found), { ...hit, file: item.path, open: true, lines: [...baseLines, t('Нажми — показать в проводнике')] }, 0, prism);
        if (found) beacons.push({ x: x + w / 2, z: z + d / 2, top: heightOf(item.size), n: found });
      } else if (item.children.length === 0) {
        place(x, z, w, d, heightOf(item.size), mat(color, found ? glow : grew ? { emissive: 0x6b1d14, emissiveIntensity: 0.9 } : {}), hit);
        if (found) beacons.push({ x: x + w / 2, z: z + d / 2, top: heightOf(item.size), n: found });
      } else {
        // Квартал: низкая платформа цвета папки + здания подпапок.
        const base = 0.7;
        place(x, z, w, d, base, mat(color.clone().multiplyScalar(0.55), grew ? { emissive: 0x7a2015, emissiveIntensity: 1 } : {}), hit);
        const inset = Math.min(0.8, Math.min(w, d) * 0.08);
        const cv = [...item.children.map(c => c.size), item.other];
        const crects = squarify(cv, { x: x + inset, y: z + inset, w: w - inset * 2, h: d - inset * 2 });
        let placed = 0;
        crects.forEach((cr, j) => {
          const cg = 0.35;
          if (cr.w - cg < 0.15 || cr.h - cg < 0.15) return;
          const child = j < item.children.length ? item.children[j] : null;
          const shade = child ? (child.isDir ? color.clone().offsetHSL(0, 0, (j % 3) * 0.05 - 0.02) : new THREE.Color('#55667e')) : new THREE.Color('#2f3f57');
          const s = child ? child.size : item.other;
          const childPath = child ? `${item.path}\\${child.name}` : null;
          // Файлы, что не попали в крупные подпапки, лежат в «остальном» квартала.
          const n = !found ? 0 : childPath ? countUnder(marks, childPath) : countRest(marks, item.path, item.children.map(c => `${item.path}\\${c.name}`));
          if (n) { beacons.push({ x: cr.x + cr.w / 2, z: cr.y + cr.h / 2, top: base + heightOf(s), n }); placed += n; }
          if (child && !child.isDir) {
            place(cr.x + cg / 2, cr.y + cg / 2, cr.w - cg, cr.h - cg, heightOf(s), fileMat(child.name, !!n), {
              path: null, file: childPath!, title: `${item.name}\\${child.name}`, lines: [bytes(s), t('Нажми — показать в проводнике')], open: true,
            }, base, prism);
            return;
          }
          place(cr.x + cg / 2, cr.y + cg / 2, cr.w - cg, cr.h - cg, heightOf(s), mat(shade, n ? glow : {}), {
            path: child?.isDir ? childPath : (item.isDir ? item.path : null),
            title: child ? `${item.name}\\${child.name}` : t('{0}: остальное', item.name),
            lines: [bytes(s), ...(child?.isDir ? [t('Нажми, чтобы открыть')] : [])],
            open: !!child?.isDir || item.isDir,
          }, base);
        });
        if (found > placed) beacons.push({ x: x + w / 2, z: z + d / 2, top: base, n: found - placed });
      }

      // Подпись крупных кварталов.
      if (w * d > 140 && labelCount++ < 9) {
        const div = document.createElement('div');
        div.className = `d3-label${grew ? ' grew' : ''}`;
        div.innerHTML = `<b></b><span></span>`;
        div.querySelector('b')!.textContent = item.name;
        div.querySelector('span')!.textContent = bytes(item.size) + (grew && item.delta ? `  +${bytes(item.delta)}` : '');
        const label = new CSS2DObject(div);
        const top = item.children.length ? Math.max(...item.children.map(c => heightOf(c.size))) + 0.7 : heightOf(item.size);
        label.position.set(x + w / 2 - W / 2, top + 2.2, z + d / 2 - D / 2);
        city.add(label);
      }
    });

    // ── Маяки над найденными файлами ──
    const beams: THREE.MeshBasicMaterial[] = [];
    if (beacons.length) {
      const beamGeo = new THREE.CylinderGeometry(0.35, 0.35, 1, 12, 1, true);
      beamGeo.translate(0, 0.5, 0);
      disposables.push(beamGeo);
      for (const b of beacons) {
        const m = new THREE.MeshBasicMaterial({ color: 0x5ce1f0, transparent: true, opacity: 0.55, depthWrite: false, blending: THREE.AdditiveBlending });
        disposables.push(m);
        beams.push(m);
        const beam = new THREE.Mesh(beamGeo, m);
        beam.position.set(b.x - W / 2, b.top, b.z - D / 2);
        beam.scale.set(1, 26, 1);
        city.add(beam);
        const div = document.createElement('div');
        div.className = 'd3-pin';
        div.textContent = String(b.n);
        div.title = t('Найдено файлов: {0}', b.n);
        const pin = new CSS2DObject(div);
        pin.position.set(b.x - W / 2, b.top + 27, b.z - D / 2);
        city.add(pin);
      }
      controls.autoRotate = false;
    }

    // ── Наведение и клик ──
    const ray = new THREE.Raycaster();
    const mouse = new THREE.Vector2();
    let hovered: THREE.Mesh | null = null;
    let downAt: { x: number; y: number } | null = null;
    const pick = (e: PointerEvent) => {
      const r = renderer.domElement.getBoundingClientRect();
      mouse.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
      ray.setFromCamera(mouse, camera);
      return (ray.intersectObjects(pickable, false)[0]?.object as THREE.Mesh | undefined) ?? null;
    };
    const setHover = (m: THREE.Mesh | null) => {
      if (hovered === m) return;
      if (hovered) (hovered.material as THREE.MeshStandardMaterial).emissiveIntensity = hovered.userData.baseEmissive ?? 0;
      hovered = m;
      if (m) {
        const mm = m.material as THREE.MeshStandardMaterial;
        m.userData.baseEmissive ??= mm.emissiveIntensity;
        if (mm.emissive.getHex() === 0) mm.emissive.set(0x2b5c66);
        mm.emissiveIntensity = Math.max(mm.emissiveIntensity, 0.8);
      }
      renderer.domElement.style.cursor = m?.userData.open ? 'pointer' : 'grab';
    };
    const onMove = (e: PointerEvent) => {
      if (downAt) return;
      const m = pick(e);
      setHover(m);
      const r = el.getBoundingClientRect();
      setTip(m ? { ...(m.userData as Hit), x: e.clientX - r.left, y: e.clientY - r.top } : null);
    };
    const onDown = (e: PointerEvent) => { downAt = { x: e.clientX, y: e.clientY }; setTip(null); };
    const onUp = (e: PointerEvent) => {
      const d = downAt;
      downAt = null;
      if (!d || Math.hypot(e.clientX - d.x, e.clientY - d.y) > 5) return; // это было вращение, а не клик
      const m = pick(e);
      const hit = m?.userData as Hit | undefined;
      if (hit?.file) api.revealPath(hit.file).catch(() => {});
      else if (hit?.path) onOpenRef.current(hit.path);
    };
    const onLeave = () => { setHover(null); setTip(null); };
    renderer.domElement.addEventListener('pointermove', onMove);
    renderer.domElement.addEventListener('pointerdown', onDown);
    renderer.domElement.addEventListener('pointerup', onUp);
    renderer.domElement.addEventListener('pointerleave', onLeave);

    const ro = new ResizeObserver(() => {
      camera.aspect = width() / Math.max(1, height());
      camera.updateProjectionMatrix();
      renderer.setSize(width(), height());
      labels.setSize(width(), height());
    });
    ro.observe(el);

    // Здания «вырастают» при открытии папки.
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    const start = performance.now();
    let raf = 0;
    const loop = () => {
      const k = reduce ? 1 : Math.min(1, (performance.now() - start) / 900);
      const ease = 1 - Math.pow(1 - k, 3);
      if (k <= 1) for (const g of growing) g.mesh.scale.y = Math.max(0.001, g.h * ease);
      if (beams.length) { const o = 0.35 + 0.25 * Math.sin(performance.now() / 350); for (const m of beams) m.opacity = o * ease; }
      controls.update();
      renderer.render(scene, camera);
      labels.render(scene, camera);
      raf = requestAnimationFrame(loop);
    };
    loop();

    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
      controls.dispose();
      renderer.domElement.removeEventListener('pointerdown', stopAuto);
      renderer.domElement.removeEventListener('pointermove', onMove);
      renderer.domElement.removeEventListener('pointerdown', onDown);
      renderer.domElement.removeEventListener('pointerup', onUp);
      renderer.domElement.removeEventListener('pointerleave', onLeave);
      disposables.forEach(x => x.dispose());
      ground.geometry.dispose(); (ground.material as THREE.Material).dispose();
      grid.geometry.dispose(); (grid.material as THREE.Material).dispose();
      renderer.dispose();
      renderer.domElement.remove();
      labels.domElement.remove();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items, other, total, marks.join('|'), basePath, files]);

  if (!webgl) return <div className="empty">{t('3D недоступно: видеокарта или драйвер не поддерживают WebGL.')}</div>;

  return (
    <div className="d3-host" ref={host} onMouseLeave={() => setTip(null)}>
      {tip && (
        <div className="tmap-tip" style={{ left: tip.x + 14, top: tip.y + 14 }}>
          <b>{tip.title}</b>
          {tip.lines.map((l, i) => <div key={i}>{l}</div>)}
        </div>
      )}
      <div className="d3-legend" aria-label={t('Обозначения')}>
        <span><i className="sq" />{t('папка')}</span>
        {FILE_TYPES.map(f => <span key={f.color}><i className="hex" style={{ background: `#${f.color.toString(16).padStart(6, '0')}` }} />{f.label()}</span>)}
        <span><i className="hex" style={{ background: `#${OTHER_FILE.toString(16)}` }} />{t('другие файлы')}</span>
      </div>
      <div className="d3-help">{t('Мышь — вращать · колесо — приблизить · правая кнопка — сдвинуть · клик — открыть папку или показать файл')}</div>
    </div>
  );
}
