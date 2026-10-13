// ============================================================================
// Gu-disk · CDI 语言包 → 属性特征库 / 界面文案
//   输入: reference/extracted/crystaldiskinfo/{Simplified Chinese,English}.lang  (UTF-16LE)
//         reference/extracted/crystaldiskmark/SimplifiedChinese.lang
//   输出: data/smart-attributes.json  (通用表 + NVMe 表 + 43 个厂商节)
//         data/i18n/zh-CN.json       (界面文案)
// ============================================================================
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJ = path.resolve(__dirname, '..');
const EXTRACT = path.join(PROJ, 'reference', 'extracted');
const DATA = path.join(PROJ, 'data');

function readLang(p) {
  const buf = fs.readFileSync(p);
  const text = (buf[0] === 0xFF && buf[1] === 0xFE)
    ? buf.toString('utf16le').replace(/^\uFEFF/, '')
    : buf.toString('utf8');
  const sections = {};
  let cur = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith(';')) continue;
    const m = line.match(/^\[([^\]]+)\]/);
    if (m) { cur = m[1].trim(); sections[cur] = sections[cur] ?? {}; continue; }
    if (cur === null) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    sections[cur][line.slice(0, eq).trim()] = line.slice(eq + 1).trim();
  }
  return sections;
}

/** 厂商节 → 型号匹配子串（用户态数据，命中后覆盖通用表） */
const VENDOR_MATCH = {
  Kioxia: ['KIOXIA', 'TOSHIBA'], Ymtc: ['YMTC'], Maxiotek: ['MAXIO'],
  Wdc: ['WDC', 'WESTERN DIGITAL', 'WD_BLACK', 'WD BLUE'],
  Samsung: ['SAMSUNG'], Intel: ['INTEL'], Micron: ['MICRON', 'CRUCIAL'],
  SanDisk: ['SANDISK'], SanDiskGb: ['SANDISK'], SanDiskHp: ['HP SSD'], SanDiskDell: ['DELL'],
  SanDiskLenovo: ['LENOVO'], SanDiskCloud: ['SANDISK'], Seagate: ['ST', 'SEAGATE'],
  Toshiba: ['TOSHIBA'], Kingston: ['KINGSTON'], SKhynix: ['SK HYNIX', 'SKHYNIX', 'HFS'],
  Phison: ['PHISON'], SiliconMotion: [], Marvell: [], SandForce: [], Maxiotek2: [],
  Realtek: [], JMicron: [], Apacer: ['APACER'], Ssstc: ['SSSTC'], Scy: ['SCY'],
  Recadata: ['RECADATA'], Ocz: ['OCZ'], Plextor: ['PLEXTOR'], Corsair: ['CORSAIR'],
  AdataIndustrial: ['ADATA'],
};

const cd = readLang(path.join(EXTRACT, 'crystaldiskinfo', 'Simplified Chinese.lang'));
const cdEn = readLang(path.join(EXTRACT, 'crystaldiskinfo', 'English.lang'));
const cdm = readLang(path.join(EXTRACT, 'crystaldiskmark', 'SimplifiedChinese.lang'));

const generic = cd['Smart'] ?? {};
const nvme = cd['SmartNVMe'] ?? {};
const genericEn = cdEn['Smart'] ?? {};

const vendors = [];
for (const [sec, table] of Object.entries(cd)) {
  if (!sec.startsWith('Smart') || sec === 'Smart' || sec === 'SmartNVMe') continue;
  const name = sec.slice(5);
  const match = VENDOR_MATCH[name] ?? [name.toUpperCase()];
  if (match.length === 0) continue;             // 控制器类厂商节（无型号可匹配）暂不启用
  vendors.push({ id: name, match, attributes: table });
}

const db = {
  schema: 1,
  generatedAt: new Date().toISOString(),
  source: 'CrystalDiskInfo ' + (cd['Information']?.VERSION ?? '?') + ' Simplified Chinese.lang',
  generic,
  genericEn,
  nvme,
  vendors: vendors.sort((a, b) => a.id.localeCompare(b.id)),
};
fs.mkdirSync(DATA, { recursive: true });
fs.writeFileSync(path.join(DATA, 'smart-attributes.json'), JSON.stringify(db, null, 2), 'utf8');

// 界面文案：只取 UI 需要的那几节，基准页从 CDM 语言包补
const i18n = {
  schema: 1,
  source: ['CrystalDiskInfo ' + (cd['Information']?.VERSION ?? '?'), 'CrystalDiskMark ' + (cdm['Information']?.VERSION ?? '?')],
  diskStatus: cd['DiskStatus'] ?? {},
  dialog: cd['Dialog'] ?? {},
  message: cd['Message'] ?? {},
  graph: cd['Graph'] ?? {},
  healthStatus: cd['HealthStatus'] ?? {},
  alarm: cd['Alarm'] ?? {},
  benchmarkMenu: cdm['Menu'] ?? {},
  benchmarkTitle: cdm['Title'] ?? {},
  benchmarkDialog: cdm['Dialog'] ?? {},
  benchmarkMessage: cdm['Message'] ?? {},
};
fs.mkdirSync(path.join(DATA, 'i18n'), { recursive: true });
fs.writeFileSync(path.join(DATA, 'i18n', 'zh-CN.json'), JSON.stringify(i18n, null, 2), 'utf8');

console.log('通用属性    :', Object.keys(generic).length, '项');
console.log('NVMe 属性   :', Object.keys(nvme).length, '项');
console.log('厂商节      :', vendors.length, '个 →', vendors.map(v => v.id + '(' + Object.keys(v.attributes).length + ')').join(' '));
console.log('界面文案节  :', Object.keys(i18n).length - 2);
console.log('输出        : data/smart-attributes.json, data/i18n/zh-CN.json');
