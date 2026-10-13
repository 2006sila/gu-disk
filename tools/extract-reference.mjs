// ============================================================================
// Gu-disk · 参照素材提取器
// 从 ../参考软件/ 下的 5 套参照软件中提取可复用的原件，落 reference/extracted/
// 移植落点见 docs/移植清单.md；本脚本只负责"取出原件 + 记账"，不做转换。
// ============================================================================
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJ = path.resolve(__dirname, '..');            // gu-disk/
const REF  = path.resolve(PROJ, '..', '参考软件');    // 参照软件所在（工作区根下的独立文件夹）
const OUT  = path.join(PROJ, 'reference', 'extracted');

// SMART Doctor 的 neutralinojs 资源包：base 偏移 + 目录表（从 exe 内读出）
const SD_EXE = path.join(REF, 'SMART_Doctor_v1.0.0_Windows_x64', 'SMART-Doctor.exe');
const SD_BASE = 2493756;
const SD_FILES = {
  'favicon.ico':          [1350,   8925],
  'icons/appIcon.png':    [10275,  16392],
  'icons/logo.gif':       [26667,  238108],
  'icons/trayIcon.png':   [264775, 965],
  'index.html':           [265740, 44251],
  'js/benchmark.js':      [309991, 11194],
  'js/disk_parser.js':    [321185, 61741],
  'js/main.js':           [382926, 65444],
  'js/mock_data.js':      [448370, 10648],
  'js/neutralino.js':     [459018, 17597],
  'js/report_exporter.js':[476615, 33473],
  'js/smart_runner.js':   [510088, 6685],
  'js/system.js':         [516773, 21667],
  'styles.css':           [538440, 45416],
};

const log = [];
let written = 0, copied = 0, failed = 0;

function ensureDir(p) { fs.mkdirSync(p, { recursive: true }); }
function sha256(buf) { return crypto.createHash('sha256').update(buf).digest('hex'); }

function emit(rel, buf, srcNote) {
  const dst = path.join(OUT, rel);
  ensureDir(path.dirname(dst));
  fs.writeFileSync(dst, buf);
  written++;
  log.push({ out: rel, bytes: buf.length, sha256: sha256(buf), source: srcNote });
  console.log(`  + ${rel}  ${buf.length} B`);
}

function copyFrom(absSrc, relDst) {
  if (!fs.existsSync(absSrc)) { failed++; log.push({ out: relDst, missing: absSrc }); console.log(`  ! 缺失 ${absSrc}`); return; }
  emit(relDst, fs.readFileSync(absSrc), path.relative(REF, absSrc));
}

console.log('== 1/4  SMART Doctor 内嵌前端（从 exe 资源包切片）==');
{
  const buf = fs.readFileSync(SD_EXE);
  for (const [rel, [off, size]] of Object.entries(SD_FILES)) {
    const slice = buf.subarray(SD_BASE + off, SD_BASE + off + size);
    emit(path.join('smartdoctor', rel), Buffer.from(slice), `SMART-Doctor.exe@${SD_BASE + off}+${size}`);
  }
}

console.log('== 2/4  CrystalDiskInfo 语言包 / 配置 ==');
{
  const b = path.join(REF, 'CrystalDiskInfo');
  copyFrom(path.join(b, 'CdiResource', 'language', 'Simplified Chinese.lang'), 'crystaldiskinfo/Simplified Chinese.lang');
  copyFrom(path.join(b, 'CdiResource', 'language', 'English.lang'),           'crystaldiskinfo/English.lang');
  copyFrom(path.join(b, 'DiskInfo.ini'),                                      'crystaldiskinfo/DiskInfo.ini');
}

console.log('== 3/4  CrystalDiskMark 语言包 / 主题 ==');
{
  const b = path.join(REF, 'CrystalDiskMark');
  copyFrom(path.join(b, 'CdmResource', 'language', 'SimplifiedChinese.lang'),  'crystaldiskmark/SimplifiedChinese.lang');
  copyFrom(path.join(b, 'DiskMark64S.ini'),                                    'crystaldiskmark/DiskMark64S.ini');
  copyFrom(path.join(b, 'DiskMark64.ini'),                                     'crystaldiskmark/DiskMark64.ini');
  copyFrom(path.join(b, 'CdmResource', 'Themes', 'Default', 'theme.ini'),      'crystaldiskmark/theme-default.ini');
}

console.log('== 4/4  smartmontools（drivedb + 随包 smartctl）==');
{
  const sd = path.join(REF, 'SMART_Doctor_v1.0.0_Windows_x64', 'tools', 'smartctl');
  copyFrom(path.join(sd, 'drivedb.h'), 'smartmontools/drivedb.h');
  // smartctl.exe 进产品 tools/，不进 reference/
  const dstDir = path.join(PROJ, 'tools', 'smartctl');
  ensureDir(dstDir);
  const src = path.join(sd, 'smartctl.exe');
  if (fs.existsSync(src)) {
    fs.copyFileSync(src, path.join(dstDir, 'smartctl.exe'));
    fs.copyFileSync(path.join(sd, 'drivedb.h'), path.join(dstDir, 'drivedb.h'));
    copied += 2;
    console.log('  + tools/smartctl/smartctl.exe  +  tools/smartctl/drivedb.h');
  } else { failed++; console.log('  ! 缺 smartctl.exe'); }
}

const manifest = {
  generatedAt: new Date().toISOString(),
  referenceRoot: REF,
  extracted: log,
  summary: { written, copied, failed },
};
fs.writeFileSync(path.join(OUT, 'MANIFEST.json'), JSON.stringify(manifest, null, 2));
console.log(`\n完成：切片 ${written} 个文件，复制 ${copied} 个，缺失 ${failed} 个`);
console.log(`清单：${path.join(OUT, 'MANIFEST.json')}`);
if (failed) process.exitCode = 2;
