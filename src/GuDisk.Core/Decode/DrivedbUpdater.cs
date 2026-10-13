using System.Diagnostics;
using System.Text;
using GuDisk.Core.App;

namespace GuDisk.Core.Decode;

/// <summary>
/// 在线更新驱动特征库（smartmontools 的 drivedb.h）。
///
/// 设计取舍：**下载之后调用已有的 Node 转换脚本，而不是在 C# 里重写一遍解析器** ——
/// 解析逻辑（家族名转义、SMR/CMR 标注）在两个语言里各维护一份迟早会跑偏，
/// 而 tools/convert-drivedb.mjs 是已经被 819 条数据验证过的那一份。
/// 没有 Node 时明确报出来，让用户手动跑一次，不假装成功。
/// </summary>
public static class DrivedbUpdater
{
    /// <summary>下载源（按顺序尝试）。国内直连 GitHub raw 常常不通，所以第二个是 jsDelivr 镜像。</summary>
    public static readonly string[] DefaultUrls =
    [
        "https://raw.githubusercontent.com/smartmontools/smartmontools/master/smartmontools/drivedb.h",
        "https://cdn.jsdelivr.net/gh/smartmontools/smartmontools@master/smartmontools/drivedb.h",
    ];

    public sealed record UpdateResult(bool Success, string? Error, string? Url,
        long Bytes, string Version, bool IndexRebuilt, string? ConverterOutput);

    /// <summary>只探测可达性，不写任何文件。</summary>
    public static async Task<UpdateResult> ProbeAsync(CancellationToken ct = default)
    {
        foreach (var url in DefaultUrls)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                long len = resp.Content.Headers.ContentLength ?? 0;
                if (len > 100_000) return new UpdateResult(true, null, url, len, "", false, null);
            }
            catch { }
        }
        return new UpdateResult(false, "两个下载源都不可达（本机可能连不上境外站点）", null, 0, "", false, null);
    }

    public static async Task<UpdateResult> UpdateAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        string? dir = AppPaths.FindSmartctlDir();
        if (dir is null) return new UpdateResult(false, "未找到 tools/smartctl 目录", null, 0, "", false, null);

        foreach (var url in DefaultUrls)
        {
            try
            {
                log?.Invoke("尝试 " + new Uri(url).Host + " …");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
                byte[] bytes = await http.GetByteArrayAsync(url, ct);
                if (bytes.Length < 100_000)
                {
                    log?.Invoke("内容仅 " + bytes.Length + " 字节，不像是 drivedb.h，跳过");
                    continue;
                }
                string text = Encoding.UTF8.GetString(bytes);
                if (!text.Contains("drivedb.h"))
                {
                    log?.Invoke("内容里没有 drivedb.h 标识，跳过");
                    continue;
                }

                string version = ExtractVersion(text);
                string target = Path.Combine(dir, "drivedb.h");
                // 备份旧的再替换（用户的既有文件不该无痕消失）
                if (File.Exists(target))
                {
                    string bak = target + ".bak";
                    try { File.Copy(target, bak, overwrite: true); } catch { }
                }
                await File.WriteAllBytesAsync(target, bytes, ct);
                log?.Invoke("已写入 " + target + "（" + bytes.Length + " 字节）");

                var (ok, output) = RunConverter(log);
                return new UpdateResult(true, null, url, bytes.Length, version, ok, output);
            }
            catch (Exception ex)
            {
                log?.Invoke("失败：" + ex.Message);
            }
        }
        return new UpdateResult(false, "两个下载源都失败了（本机可能连不上境外站点）", null, 0, "", false, null);
    }

    /// <summary>从 `$Id: drivedb.h 5714 2025-04-30 ...` 里取版本串。</summary>
    private static string ExtractVersion(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf("drivedb.h", StringComparison.Ordinal);
            if (i < 0) continue;
            var tail = line[(i + 9)..].Trim();
            if (tail.Length > 0) return tail.Split(' ')[0] + " " + (tail.Split(' ').Length > 1 ? tail.Split(' ')[1] : "");
        }
        return "未知";
    }

    /// <summary>调用 tools/convert-drivedb.mjs 重建索引（需要 Node）。</summary>
    private static (bool Ok, string Output) RunConverter(Action<string>? log)
    {
        string? script = AppPaths.FindTool("convert-drivedb.mjs");
        if (script is null)
        {
            return (false, "未找到 tools/convert-drivedb.mjs，drivedb-index.json 未重建");
        }
        try
        {
            var psi = new ProcessStartInfo("node", "\"" + script + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "无法启动 node");
            string outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            log?.Invoke("转换脚本：" + (p.ExitCode == 0 ? "成功" : "退出码 " + p.ExitCode));
            return (p.ExitCode == 0, outText.Trim());
        }
        catch (Exception ex)
        {
            return (false, "跑转换脚本失败：" + ex.Message + "（需要 Node 环境，手动跑 node tools/convert-drivedb.mjs 即可）");
        }
    }
}
