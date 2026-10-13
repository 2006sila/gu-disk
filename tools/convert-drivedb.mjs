// ============================================================================
// Gu-disk · drivedb.h -> 型号索引
//   输入: reference/extracted/smartmontools/drivedb.h
//   输出: data/drivedb-index.json  [{ family, pattern, smr, cmr }]
//
// 实现说明：本脚本刻意不使用任何正则字面量 —— drivedb 的族名里含引号转义与括号，
// 用字符串扫描比正则稳，也避免转义层级出错。SMR/CMR 直接看族名里的标注：
//   例如 "Seagate BarraCuda 3.5 (SMR)" / "Western Digital Blue (CMR)"。
// ============================================================================
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const LF = String.fromCharCode(10);
const CR = String.fromCharCode(13);
const QUOTE = String.fromCharCode(34);
const BS = String.fromCharCode(92);

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJ = path.resolve(__dirname, '..');
const src = path.join(PROJ, 'reference', 'extracted', 'smartmontools', 'drivedb.h');
const out = path.join(PROJ, 'data', 'drivedb-index.json');

const raw = fs.readFileSync(src, 'utf8');
const lines = raw.split(LF).map(l => (l.endsWith(CR) ? l.slice(0, -1) : l));

let version = '';
const vi = raw.indexOf('drivedb.h - smartmontools ');
if (vi >= 0) version = raw.slice(vi + 'drivedb.h - smartmontools '.length).split(' ')[0];

/** 取一行里第一个双引号字符串（处理反斜杠转义）；返回 { text, endIndex } */
function firstQuoted(line, fromIndex) {
  const start = line.indexOf(QUOTE, fromIndex ?? 0);
  if (start < 0) return null;
  let acc = '';
  for (let j = start + 1; j < line.length; j++) {
    const ch = line[j];
    if (ch === BS) { acc += line[j + 1] ?? ''; j++; continue; }
    if (ch === QUOTE) return { text: acc, endIndex: j };
    acc += ch;
  }
  return null;
}

const entries = [];
for (let i = 0; i < lines.length; i++) {
  const head = lines[i].trimStart();
  if (!head.startsWith('{')) continue;

  const fam = firstQuoted(head, 1);
  if (!fam) continue;

  // 型号正则：往下跳过空行与注释行，取第一条以引号开头的行
  let pattern = '';
  for (let j = i + 1; j < Math.min(i + 10, lines.length); j++) {
    const t = lines[j].trimStart();
    if (t.length === 0) continue;
    if (t.startsWith('//')) continue;
    if (t.startsWith(QUOTE)) { const q = firstQuoted(t); if (q) pattern = q.text; }
    break;
  }
  if (!pattern) continue;

  const family = fam.text;
  entries.push({ family, pattern, smr: family.includes('(SMR)'), cmr: family.includes('(CMR)') });
}

const smr = entries.filter(e => e.smr);
const cmr = entries.filter(e => e.cmr);
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, JSON.stringify({
  schema: 1,
  generatedAt: new Date().toISOString(),
  source: 'smartmontools drivedb.h ' + (version || '?'),
  total: entries.length,
  smrEntries: smr.length,
  cmrEntries: cmr.length,
  entries,
}, null, 1), 'utf8');

console.log('drivedb 版本 :', version);
console.log('型号条目     :', entries.length);
console.log('SMR 标注     :', smr.length, '  CMR 标注:', cmr.length);
console.log('SMR 条目：');
for (const e of smr) console.log('   ', e.family, ' -> ', e.pattern.slice(0, 52));
console.log('CMR 条目：');
for (const e of cmr) console.log('   ', e.family);
console.log('输出         : data/drivedb-index.json');
