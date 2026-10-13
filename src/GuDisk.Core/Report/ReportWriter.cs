using System.Globalization;
using System.Text;
using GuDisk.Core.App;
using GuDisk.Core.Benchmark;
using GuDisk.Core.Decode;
using GuDisk.Core.Health;
using GuDisk.Core.Scan;

namespace GuDisk.Core.Report;

/// <summary>报告的输入：诊断结果 + 可选的测速/曲线/扫描/温度历史。</summary>
public sealed record ReportInput(
    DriveDiagnostic Diag,
    BenchResult? Bench = null,
    CurveResult? Curve = null,
    ScanResult? Scan = null,
    IReadOnlyList<(DateTime When, double ValueCelsius)>? TemperatureHistory = null)
{
    public string GeneratedAt { get; init; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// 诊断报告导出：HTML（自包含单文件，配色沿用界面规范）/ TXT / JSON。
/// 报告的目标是"能直接发给别人看懂"，所以顺序是：结论 → 依据 → 明细。
/// </summary>
public static class ReportWriter
{
    // ---------------- TXT ----------------
    public static string ToText(ReportInput r)
    {
        var d = r.Diag;
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        void KV(string k, string v) => L("  " + Pad(k, 14) + v);
        string Pad(string s, int n)
        {
            int w = s.Sum(c => c > 0x2E80 ? 2 : 1);
            return s + new string(' ', Math.Max(1, n - w));
        }

        L("=".PadRight(72, '='));
        L("  Gu-disk 磁盘诊断报告");
        L("  生成时间：" + r.GeneratedAt + "    工具版本：" + Core.GuDiskInfo.Version);
        L("=".PadRight(72, '='));
        L();
        L("【一、设备识别】");
        KV("型号", d.Drive.Model);
        KV("序列号", d.Drive.Serial);
        KV("固件", d.Drive.Firmware);
        KV("容量", d.Drive.CapacityText);
        KV("总线 / 协议", d.Drive.BusText + " / " + d.Drive.Protocol);
        KV("盘符", d.Drive.MountPoints.Count > 0 ? string.Join(" ", d.Drive.MountPoints) : "（无）");
        KV("系统盘", d.Drive.IsSystemDisk ? "是" : "否");
        KV("控制器", d.Drive.AdapterName);
        KV("DiskKey", d.Drive.DiskKey);
        if (d.Drive.IsUsb)
        {
            KV("桥接主控", d.Drive.BridgeName ?? "未收录");
            KV("判定依据", d.Drive.BridgeEvidence ?? "-");
        }
        L();

        L("【二、状态判定】");
        KV("总体状态", d.Verdict.Label + "    （" + d.Verdict.CountsText + "）");
        KV("结论", d.Verdict.Summary);
        KV("介质类型", d.Media.Label + "  置信度 " + d.Media.ConfidencePercent + "%");
        KV("判定依据", d.Media.Reason);
        L();

        if (d.Verdict.Findings.Count > 0)
        {
            L("【三、判定依据（异常项）】");
            foreach (var f in d.Verdict.Findings)
            {
                L("  [" + GuDisk.Core.Health.StatusVerdictEvaluator.LabelOf(f.Level) + "] " + f.Source
                  + " = " + f.RawValue);
                L("        " + f.Reading);
                if (f.Advice is not null) L("        建议：" + f.Advice);
            }
            L();
        }
        else L("【三、判定依据】未发现异常项。" + Environment.NewLine);

        if (d.Verdict.Checks.Count > 0)
        {
            L("【已核对项（均正常）】");
            foreach (var c in d.Verdict.Checks) L("  · " + c.Source + " = " + c.RawValue);
            L();
        }

        if (r.TemperatureHistory is { Count: > 0 })
        {
            L("【温度历史】");
            var temps = r.TemperatureHistory.Select(x => x.ValueCelsius).ToList();
            KV("采样点", r.TemperatureHistory.Count + " 个");
            KV("范围", temps.Min().ToString("0.#") + " ~ " + temps.Max().ToString("0.#") + " ℃，平均 "
                + temps.Average().ToString("0.#") + " ℃");
            KV("最近", r.TemperatureHistory[^1].When.ToString("yyyy-MM-dd HH:mm") + "  "
                + r.TemperatureHistory[^1].ValueCelsius.ToString("0.#") + " ℃");
            L();
        }

        if (r.Bench?.Success == true)
        {
            L("【基准测速】");
            KV("测试文件", UnitConverter.FormatBytes(r.Bench.FileSize) + "（" + r.Bench.Pattern + " 数据）");
            foreach (var row in r.Bench.Results)
                L("  " + Pad(row.Label + " " + row.Mode, 24)
                  + (row.Success ? row.MibPerSecond.ToString("N1") + " MB/s   "
                        + row.Iops.ToString("N0") + " IOPS   " + row.LatencyMs.ToString("0.000") + " ms"
                    : "失败：" + row.Error));
            L();
        }

        if (r.Curve?.Success == true)
        {
            L("【顺序读曲线】");
            KV("范围", UnitConverter.FormatBytes(r.Curve.TotalBytes) + "（" + r.Curve.Mode + "）");
            KV("速度", "平均 " + r.Curve.AverageMibPerSecond.ToString("N0") + " · 最低 "
                + r.Curve.MinMibPerSecond.ToString("N0") + " · 最高 " + r.Curve.MaxMibPerSecond.ToString("N0") + " MB/s");
            KV("判读", r.Curve.Diagnosis);
            L();
        }

        if (r.Scan?.Success == true)
        {
            L("【坏道扫描】");
            KV("模式", r.Scan.Mode);
            KV("已扫描", UnitConverter.FormatBytes(r.Scan.ScannedBytes) + "（" + r.Scan.ScannedBlocks + " 块）");
            KV("结果", "坏块 " + r.Scan.ErrorCount + " · 偏慢 " + r.Scan.SlowCount);
            KV("结论", r.Scan.Conclusion);
            foreach (var b in r.Scan.ProblemBlocks.Take(20))
                L("  0x" + b.OffsetBytes.ToString("X12") + "  " + b.Class + "  " + b.Explanation);
            if (r.Scan.ProblemBlocks.Count > 20) L("  …（共 " + r.Scan.ProblemBlocks.Count + " 条）");
            L();
        }

        if (d.Smart.Attributes.Count > 0)
        {
            L("【属性明细】");
            L("  ID   名称                           原始值                          状态");
            foreach (var a in d.Smart.Attributes)
                L("  " + a.Id.ToString("X2") + "   " + Pad(a.NameCn, 30) + Pad(a.RawMeaning, 30) + a.Status);
            L();
        }

        L("【数据来源】");
        KV("引擎", d.Smart.Engine + (d.Smart.Success ? "" : "（读取失败：" + d.Smart.Error + "）"));
        if (d.CrossCheck.Count > 0)
            KV("交叉校验", d.CrossCheck.Count + " 项，" + (d.Disagreements.Count == 0 ? "全部一致" : d.Disagreements.Count + " 项不一致"));
        KV("属性库", d.AttributeDbSource);
        KV("驱动库", d.DrivedbSource);
        L();
        L("【建议】");
        var advice = d.Verdict.Findings.Where(f => f.Advice is not null).Select(f => f.Advice!).Distinct().ToList();
        if (advice.Count == 0) L("  · 未发现需要处理的问题，按常规备份习惯使用即可。");
        foreach (var rec in advice) L("  · " + rec);
        L();
        L("-".PadRight(72, '-'));
        L("  本报告由 Gu-disk 自动生成。状态判定全部来自 SMART 原始字段的逐条谓词判断，不含综合评分，不构成硬件保修依据。");
        return sb.ToString();
    }

    // ---------------- JSON ----------------
    public static string ToJson(ReportInput r)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            tool = new { name = Core.GuDiskInfo.ProductName, version = Core.GuDiskInfo.Version },
            generatedAt = r.GeneratedAt,
            drive = r.Diag.Drive,
            verdict = r.Diag.Verdict,
            media = r.Diag.Media,
            smart = new
            {
                success = r.Diag.Smart.Success,
                engine = r.Diag.Smart.Engine,
                error = r.Diag.Smart.Error,
                source = r.Diag.Smart.Source,
                attempts = r.Diag.Smart.Attempts,
                nvme = r.Diag.Smart.Nvme,
                attributes = r.Diag.Smart.Attributes,
            },
            crossCheck = r.Diag.CrossCheck,
            temperatureHistory = r.TemperatureHistory,
            bench = r.Bench,
            curve = r.Curve,
            scan = r.Scan,
            sources = new { attributes = r.Diag.AttributeDbSource, drivedb = r.Diag.DrivedbSource },
            notes = r.Diag.Notes,
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    // ---------------- HTML ----------------
    public static string ToHtml(ReportInput r)
    {
        var d = r.Diag;
        var v = d.Verdict;
        var sb = new StringBuilder();
        string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(Esc(d.Drive.Model)).Append(" · Gu-disk 诊断报告</title><style>");
        sb.Append(HtmlCss);
        sb.Append("</style></head><body><div class=\"page\">");

        // 顶栏
        sb.Append("<header><div class=\"brand\"><span class=\"mark\"></span>");
        sb.Append("<b>Gu-disk</b><span class=\"sub\">磁盘全检</span></div>");
        sb.Append("<div class=\"meta\">生成于 ").Append(Esc(r.GeneratedAt))
          .Append(" · v").Append(Core.GuDiskInfo.Version).Append("</div></header>");

        // 结论横幅
        string stateCls = v.State switch
        {
            DriveState.Normal => "ok",
            DriveState.Attention => "warn",
            DriveState.Warning => "warn",
            _ => "bad"
        };
        sb.Append("<section class=\"hero\"><div class=\"score ").Append(stateCls).Append("\">")
          .Append("<span class=\"num\" style=\"font-size:44px;letter-spacing:0\">").Append(Esc(v.Label)).Append("</span>")
          .Append("<span class=\"grade\">").Append(Esc(v.CountsText)).Append("</span></div>")
          .Append("<div class=\"verdict\"><div class=\"model\">").Append(Esc(d.Drive.Model)).Append("</div>")
          .Append("<p>").Append(Esc(v.Summary)).Append("</p>")
          .Append("<div class=\"chips\">")
          .Append("<span class=\"chip\">").Append(Esc(d.Media.Label)).Append("</span>")
          .Append("<span class=\"chip\">").Append(Esc(d.Drive.BusText)).Append("</span>")
          .Append("<span class=\"chip\">").Append(Esc(d.Drive.CapacityText)).Append("</span>")
          .Append("<span class=\"chip\">引擎 ").Append(Esc(d.Smart.Engine)).Append("</span>")
          .Append("</div></div></section>");

        // 关键指标
        sb.Append("<section><h2>设备与状态</h2><div class=\"grid\">");
        void Cell(string k, string v) =>
            sb.Append("<div class=\"cell\"><span class=\"k\">").Append(Esc(k))
              .Append("</span><span class=\"v\">").Append(Esc(v)).Append("</span></div>");
        Cell("序列号", d.Drive.Serial);
        Cell("固件", d.Drive.Firmware);
        Cell("协议", d.Drive.Protocol.ToString());
        Cell("盘符", d.Drive.MountPoints.Count > 0 ? string.Join(" ", d.Drive.MountPoints) : "（无）");
        Cell("系统盘", d.Drive.IsSystemDisk ? "是" : "否");
        Cell("控制器", string.IsNullOrEmpty(d.Drive.AdapterName) ? "-" : d.Drive.AdapterName);
        if (d.Smart.Nvme is { } n)
        {
            Cell("综合温度", (n.CompositeTemperatureC?.ToString("0.#") ?? "-") + " ℃");
            Cell("已用寿命", n.PercentageUsed + " %");
            Cell("可用备用空间", n.AvailableSpare + " %");
            Cell("通电时间", UnitConverter.FormatCount((long)n.PowerOnHours) + " 小时");
            Cell("主机读取", UnitConverter.FormatBytes(UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsRead)));
            Cell("主机写入", UnitConverter.FormatBytes(UnitConverter.NvmeDataUnitsToBytes(n.DataUnitsWritten)));
        }
        sb.Append("</div></section>");

        // 介质判定
        sb.Append("<section><h2>介质类型判定</h2><p class=\"note\">")
          .Append(Esc(d.Media.Label)).Append(" · 置信度 ").Append(d.Media.ConfidencePercent)
          .Append("%<br>依据：").Append(Esc(d.Media.Reason)).Append("</p></section>");

        // 判定依据
        sb.Append("<section><h2>判定依据</h2>");
        if (v.Findings.Count == 0)
            sb.Append("<p class=\"note\">未发现异常项。已核对 ").Append(v.Checks.Count)
              .Append(" 项，均在正常范围。</p>");
        else
        {
            sb.Append("<table><thead><tr><th>级别</th><th>来源属性</th><th>原始值</th><th>判读</th><th>建议</th></tr></thead><tbody>");
            foreach (var f in v.Findings)
            {
                string lv = f.Level switch
                {
                    DriveState.Normal => "muted",
                    DriveState.Attention => "warn",
                    DriveState.Warning => "warn",
                    _ => "bad"
                };
                sb.Append("<tr><td><span class=\"tag ").Append(lv).Append("\">")
                  .Append(Esc(StatusVerdictEvaluator.LabelOf(f.Level))).Append("</span></td><td>")
                  .Append(Esc(f.Source)).Append("</td><td class=\"mono\">").Append(Esc(f.RawValue))
                  .Append("</td><td>").Append(Esc(f.Reading)).Append("</td><td class=\"advice\">")
                  .Append(Esc(f.Advice ?? "")).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        if (v.Checks.Count > 0)
        {
            sb.Append("<p class=\"note\"><b>已核对且正常（").Append(v.Checks.Count).Append(" 项）：</b>");
            sb.Append(string.Join("　", v.Checks.Select(c => Esc(c.Source) + " = " + Esc(c.RawValue))));
            sb.Append("</p>");
        }
        sb.Append("</section>");

        // 温度曲线
        if (r.TemperatureHistory is { Count: > 1 })
            sb.Append("<section><h2>温度历史</h2>")
              .Append(Sparkline(r.TemperatureHistory))
              .Append("</section>");

        // 测速
        if (r.Bench?.Success == true)
        {
            sb.Append("<section><h2>基准测速</h2><table><thead><tr><th>档位</th><th>模式</th>")
              .Append("<th>吞吐量</th><th>IOPS</th><th>平均延迟</th></tr></thead><tbody>");
            foreach (var row in r.Bench.Results)
                sb.Append("<tr><td><b>").Append(Esc(row.Label)).Append("</b></td><td>").Append(Esc(row.Mode))
                  .Append("</td><td>").Append(row.Success ? row.MibPerSecond.ToString("N1") + " MB/s" : Esc(row.Error ?? ""))
                  .Append("</td><td>").Append(row.Iops.ToString("N0")).Append("</td><td>")
                  .Append(row.LatencyMs.ToString("0.000")).Append(" ms</td></tr>");
            sb.Append("</tbody></table><p class=\"note\">测试文件 ")
              .Append(UnitConverter.FormatBytes(r.Bench.FileSize)).Append(" · ").Append(Esc(r.Bench.Pattern))
              .Append(" 数据 · 无缓存直通 I/O</p></section>");
        }

        // 曲线
        if (r.Curve?.Success == true)
        {
            sb.Append("<section><h2>顺序读曲线</h2>")
              .Append(CurveChart(r.Curve))
              .Append("<p class=\"note\">平均 ").Append(r.Curve.AverageMibPerSecond.ToString("N0"))
              .Append(" MB/s · 最低 ").Append(r.Curve.MinMibPerSecond.ToString("N0"))
              .Append(" · 最高 ").Append(r.Curve.MaxMibPerSecond.ToString("N0"))
              .Append(" MB/s<br>").Append(Esc(r.Curve.Diagnosis)).Append("</p></section>");
        }

        // 坏道
        if (r.Scan?.Success == true)
        {
            sb.Append("<section><h2>坏道扫描</h2><p class=\"note\">").Append(Esc(r.Scan.Mode))
              .Append(" · 已扫描 ").Append(UnitConverter.FormatBytes(r.Scan.ScannedBytes))
              .Append("（").Append(r.Scan.ScannedBlocks).Append(" 块）· <b>坏块 ").Append(r.Scan.ErrorCount)
              .Append(" · 偏慢 ").Append(r.Scan.SlowCount).Append("</b><br>").Append(Esc(r.Scan.Conclusion))
              .Append("</p>");
            if (r.Scan.ProblemBlocks.Count > 0)
            {
                sb.Append("<table><thead><tr><th>偏移</th><th>类型</th><th>速度</th><th>错误码</th><th>解释</th></tr></thead><tbody>");
                foreach (var b in r.Scan.ProblemBlocks.Take(30))
                    sb.Append("<tr><td class=\"mono\">0x").Append(b.OffsetBytes.ToString("X12"))
                      .Append("</td><td>").Append(b.Class).Append("</td><td>")
                      .Append(b.MibPerSecond.ToString("N0")).Append(" MB/s</td><td>").Append(b.Win32Error)
                      .Append("</td><td>").Append(Esc(b.Explanation)).Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");
        }

        // 属性表
        if (d.Smart.Attributes.Count > 0)
        {
            sb.Append("<section><h2>SMART 属性明细（").Append(d.Smart.Attributes.Count).Append(" 项）</h2>")
              .Append("<table><thead><tr><th>ID</th><th>名称</th><th>原始值</th><th>状态</th></tr></thead><tbody>");
            foreach (var a in d.Smart.Attributes)
                sb.Append("<tr><td class=\"mono\">").Append(a.Id.ToString("X2")).Append("</td><td>")
                  .Append(Esc(a.NameCn)).Append("</td><td class=\"mono\">").Append(Esc(a.RawMeaning))
                  .Append("</td><td>").Append(StatusChip(a.Status)).Append("</td></tr>");
            sb.Append("</tbody></table></section>");
        }

        // 来源与建议
        sb.Append("<section><h2>数据来源与建议</h2><p class=\"note\">")
          .Append("引擎 ").Append(Esc(d.Smart.Engine));
        if (d.CrossCheck.Count > 0)
            sb.Append(" · 双引擎交叉校验 ").Append(d.CrossCheck.Count).Append(" 项（")
              .Append(d.Disagreements.Count == 0 ? "全部一致" : d.Disagreements.Count + " 项不一致").Append("）");
        sb.Append("<br>属性库 ").Append(Esc(d.AttributeDbSource))
          .Append("<br>驱动库 ").Append(Esc(d.DrivedbSource)).Append("</p><ul>");
        var adviceList = v.Findings.Where(f => f.Advice is not null).Select(f => f.Advice!).Distinct().ToList();
        if (adviceList.Count == 0) adviceList.Add("未发现需要处理的问题，按常规备份习惯使用即可。");
        foreach (var rec in adviceList) sb.Append("<li>").Append(Esc(rec)).Append("</li>");
        sb.Append("</ul></section>");

        sb.Append("<footer>本报告由 Gu-disk 自动生成。状态判定全部来自 SMART 原始字段的逐条谓词判断，不含综合评分，不构成硬件保修依据。</footer>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string StatusChip(string status)
    {
        string cls = status switch
        {
            "正常" => "ok", "注意" => "warn", "危险" => "bad", _ => "muted"
        };
        return "<span class=\"tag " + cls + "\">" + System.Net.WebUtility.HtmlEncode(status) + "</span>";
    }

    /// <summary>温度迷你折线（内嵌 SVG，不引任何库）。</summary>
    private static string Sparkline(IReadOnlyList<(DateTime When, double ValueCelsius)> pts)
    {
        const int W = 900, H = 160, P = 24;
        double min = pts.Min(p => p.ValueCelsius), max = pts.Max(p => p.ValueCelsius);
        if (max - min < 1) { min -= 1; max += 1; }
        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append(W).Append(' ').Append(H)
          .Append("\" width=\"100%\" style=\"height:160px\">");
        sb.Append("<rect width=\"").Append(W).Append("\" height=\"").Append(H).Append("\" fill=\"#121417\"/>");
        for (int g = 0; g <= 4; g++)
        {
            double y = P + (H - 2 * P) * g / 4.0;
            double v = max - (max - min) * g / 4.0;
            sb.Append("<line x1=\"").Append(P).Append("\" y1=\"").Append(y.ToString("0")).Append("\" x2=\"")
              .Append(W - P).Append("\" y2=\"").Append(y.ToString("0"))
              .Append("\" stroke=\"#262A30\" stroke-dasharray=\"3 5\"/>");
            sb.Append("<text x=\"2\" y=\"").Append((y + 4).ToString("0"))
              .Append("\" fill=\"#6B7480\" font-size=\"11\">").Append(v.ToString("0")).Append("</text>");
        }
        var line = new StringBuilder();
        for (int i = 0; i < pts.Count; i++)
        {
            double x = P + (W - 2.0 * P) * i / Math.Max(1, pts.Count - 1);
            double y = H - P - (H - 2.0 * P) * (pts[i].ValueCelsius - min) / (max - min);
            if (i > 0) line.Append(' ');
            line.Append(x.ToString("0.0")).Append(',').Append(y.ToString("0.0"));
        }
        sb.Append("<polyline points=\"").Append(line).Append("\" fill=\"none\" stroke=\"#C6F24E\" stroke-width=\"2\"/>");
        sb.Append("<text x=\"").Append(P).Append("\" y=\"").Append(H - 6)
          .Append("\" fill=\"#6B7480\" font-size=\"11\">").Append(pts[0].When.ToString("yyyy-MM-dd HH:mm")).Append("</text>");
        sb.Append("<text x=\"").Append(W - P - 120).Append("\" y=\"").Append(H - 6)
          .Append("\" fill=\"#6B7480\" font-size=\"11\">").Append(pts[^1].When.ToString("yyyy-MM-dd HH:mm")).Append("</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>顺序曲线（内嵌 SVG 柱状）。</summary>
    private static string CurveChart(CurveResult c)
    {
        const int W = 900, H = 180, P = 30;
        double max = Math.Max(1, c.MaxMibPerSecond);
        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append(W).Append(' ').Append(H)
          .Append("\" width=\"100%\" style=\"height:180px\">");
        sb.Append("<rect width=\"").Append(W).Append("\" height=\"").Append(H).Append("\" fill=\"#121417\"/>");
        int n = c.Points.Count;
        double bw = (W - 2.0 * P) / Math.Max(1, n);
        for (int i = 0; i < n; i++)
        {
            double hgt = (H - 2.0 * P) * c.Points[i].MibPerSecond / max;
            double x = P + i * bw;
            sb.Append("<rect x=\"").Append(x.ToString("0.0")).Append("\" y=\"").Append((H - P - hgt).ToString("0.0"))
              .Append("\" width=\"").Append(Math.Max(1, bw - 1).ToString("0.0")).Append("\" height=\"")
              .Append(hgt.ToString("0.0")).Append("\" fill=\"#C6F24E\" opacity=\"0.85\"/>");
        }
        sb.Append("<text x=\"").Append(P).Append("\" y=\"").Append(H - 8)
          .Append("\" fill=\"#6B7480\" font-size=\"11\">0%</text>");
        sb.Append("<text x=\"").Append(W - P - 40).Append("\" y=\"").Append(H - 8)
          .Append("\" fill=\"#6B7480\" font-size=\"11\">100%</text>");
        sb.Append("<text x=\"").Append(P).Append("\" y=\"14\" fill=\"#6B7480\" font-size=\"11\">")
          .Append(max.ToString("N0")).Append(" MB/s</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private const string HtmlCss = @"
*{box-sizing:border-box;margin:0;padding:0}
body{background:#0B0C0E;color:#ECEEF1;font:14px/1.6 'Segoe UI','Microsoft YaHei',sans-serif;padding:28px 16px}
.page{max-width:1000px;margin:0 auto}
header{display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #262A30;padding-bottom:14px;margin-bottom:20px}
.brand{display:flex;align-items:center;gap:9px}
.mark{width:20px;height:20px;border:2px solid #C6F24E;border-radius:6px;display:inline-block}
.brand b{font-size:17px;letter-spacing:.5px}
.sub{font-size:11px;color:#C6F24E;background:rgba(198,242,78,.13);padding:2px 8px;border-radius:4px}
.meta{font-size:12px;color:#6B7480}
section{margin-bottom:22px}
h2{font-size:12px;letter-spacing:1.5px;color:#6B7480;font-weight:700;margin-bottom:10px}
.hero{display:flex;gap:26px;align-items:center;background:#121417;border:1px solid #262A30;border-radius:10px;padding:18px 20px}
.score{display:flex;align-items:baseline;gap:4px;flex:none}
.score .num{font-size:54px;font-weight:700;letter-spacing:-2px}
.score.ok .num{color:#35D07F}.score.warn .num{color:#E8B341}.score.bad .num{color:#FF5F56}
.score .unit{font-size:13px;color:#6B7480}
.score .grade{font-size:13px;font-weight:700;margin-left:8px;color:#9BA3AE}
.verdict .model{font-size:16px;font-weight:600;margin-bottom:4px}
.verdict p{color:#9BA3AE;font-size:13px}
.chips{display:flex;gap:8px;margin-top:10px;flex-wrap:wrap}
.chip{font-size:11px;color:#9BA3AE;border:1px solid #262A30;border-radius:99px;padding:2px 10px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:1px;background:#262A30;border:1px solid #262A30;border-radius:8px;overflow:hidden}
.cell{background:#121417;padding:10px 14px}
.cell .k{display:block;font-size:11px;color:#6B7480}
.cell .v{font-weight:600;font-size:13.5px}
table{width:100%;border-collapse:collapse;background:#121417;border:1px solid #262A30;border-radius:8px;overflow:hidden;font-size:13px}
th{text-align:left;font-size:11px;letter-spacing:.8px;color:#6B7480;padding:9px 12px;background:#171A1E;border-bottom:1px solid #262A30}
td{padding:8px 12px;border-bottom:1px solid #1E2228;vertical-align:top}
tr:last-child td{border-bottom:0}
.mono{font-family:Consolas,monospace}
.pen{color:#FF5F56;font-weight:700}
.advice{color:#9BA3AE}
.note{font-size:12.5px;color:#9BA3AE}
.tag{font-size:11px;font-weight:600;padding:2px 8px;border-radius:99px;white-space:nowrap}
.tag.ok{color:#35D07F;background:rgba(53,208,127,.13)}
.tag.warn{color:#E8B341;background:rgba(232,179,65,.13)}
.tag.bad{color:#FF5F56;background:rgba(255,95,86,.13)}
.tag.muted{color:#9BA3AE;background:rgba(155,163,174,.12)}
ul{margin:8px 0 0 20px;color:#9BA3AE;font-size:13px}
svg{display:block;border:1px solid #262A30;border-radius:8px}
footer{margin-top:26px;padding-top:14px;border-top:1px solid #262A30;font-size:11.5px;color:#6B7480}
";
}
