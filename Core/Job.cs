namespace NeteaseGameDownloader.Core;

using System;
using System.IO;
using System.Threading.Tasks;
using NeteaseGameDownloader.Models;

/// <summary>
/// 一次完整的下载任务（界面无关，对应 Python 的 Job）。
/// 串起：解析最新版 → 探测 → 已存在文件确认 → 下载 → 即时校验。
/// </summary>
public sealed class Job(JobConfig config, JobStatus status, Func<string, bool, bool>? confirm = null)
{
    private readonly JobConfig _config = config;
    private readonly JobStatus _status = status;
    private readonly Func<string, bool, bool>? _confirm = confirm;
    private DownloadEngine? _engine;
    private Resolution? _resolution;

    // ---------------- 控制 ----------------
    public void Pause() => _engine?.Pause();
    public void Resume() => _engine?.Resume();
    public void Cancel()
    {
        _engine?.Cancel();
        _status.AnswerConfirm(false);
    }

    private bool Confirm(string prompt, bool defaultYes)
    {
        if (_config.AssumeYes) return true;
        if (_confirm is not null) return _confirm(prompt, defaultYes);
        return _status.Ask(prompt, defaultYes, timeout: 300);
    }

    // ---------------- 执行 ----------------
    public async Task<int> RunAsync()
    {
        _status.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        LogHub.AddSink(_status.Log);
        int code;
        try
        {
            code = await RunInnerAsync();
            _status.Result(code == 0, code == 0 ? "" : _status.Error);
            return code;
        }
        catch (OperationCanceledException)
        {
            _status.Phase(Phases.Canceled, "已中断（进度已保存，可续传）");
            _status.Error = "已中断";
            _status.Result(false, "已中断");
            code = 130;
            return code;
        }
        catch (DownloadEngineException e)
        {
            _status.Phase(Phases.Failed, e.Message);
            _status.Error = e.Message;
            Utils.LogError($"任务异常: {e.Message}");
            _status.Result(false, e.Message);
            code = 1;
            return code;
        }
        catch (Exception e)
        {
            _status.Phase(Phases.Failed, $"{e.GetType().Name}: {e.Message}");
            _status.Error = $"{e.GetType().Name}: {e.Message}";
            Utils.LogError($"任务异常: {e.GetType().Name}: {e.Message}");
            _status.Result(false, _status.Error);
            code = 1;
            return code;
        }
        finally
        {
            LogHub.RemoveSink(_status.Log);
            lock (_status) { _status.Finished = true; }
        }
    }

    private async Task<int> RunInnerAsync()
    {
        var cfg = _config;
        var product = Products.GetProduct(cfg.ProductKey);
        lock (_status)
        {
            _status.ProductKey = product.Key;
            _status.ProductName = product.DisplayName;
            _status.ProductDesc = product.Desc;
            _status.Platform = product.Platform;
            _status.IsInfo = cfg.InfoOnly;
        }

        // 1. 解析最新下载地址
        _status.Phase(Phases.Resolve, product.DisplayName);
        Utils.LogInfo($"产品: {product.DisplayName}");
        if (!string.IsNullOrWhiteSpace(cfg.Url))
            Utils.LogInfo($"使用手动指定的下载地址: {cfg.Url}");
        var res = await Resolver.ResolveProductAsync(product, cfg.Url);
        _resolution = res;
        lock (_status)
        {
            _status.Url = res.Url;
            _status.Source = res.Source;
            _status.Version = res.Version;
            _status.Notes = [.. res.Notes];
            _status.Attempts = [.. res.Attempts];
        }
        foreach (var note in res.Notes) Utils.LogInfo(note);

        // 2. 版本交叉核对
        await CrossCheckVersion(product);

        var outputPath = Path.GetFullPath(cfg.Output ?? product.DefaultOutput);
        lock (_status) _status.OutputPath = outputPath;

        // 3. 仅看信息
        if (cfg.InfoOnly)
            return await ShowInfo(product, res, outputPath);

        // 4. 已有完整文件时的处理
        if (!HandleExisting(outputPath))
            return _status.Ok == false && !string.IsNullOrEmpty(_status.Error) ? 1 : 0;

        // 5. 下载 + 即时校验
        _engine = new DownloadEngine(
            url: res.Url,
            outputPath: outputPath,
            threads: cfg.ThreadValue,
            retries: cfg.Retries,
            expectedMd5: cfg.ExpectedMd5,
            forceRestart: cfg.Force,
            autoVerify: cfg.Verify,
            keepChecksum: cfg.KeepChecksum,
            sink: _status,
            urlRefresher: cfg.Url is null ? (Func<Task<string?>>?)(() => Resolver.RefreshUrlAsync(res)) : null);
        lock (_status) _status.Control = _engine;
        Utils.LogInfo($"线程: {(cfg.AutoThreads ? "auto（自适应）" : $"{cfg.Threads}（上限，小文件自动收敛）")}" +
                      $" | 输出: {_engine.OutputPath}");
        var ok = await _engine.DownloadAsync();
        lock (_status)
        {
            _status.OutputPath = _engine.OutputPath;
            if (_engine.Result is not null)
            {
                var r = _engine.Result;
                _status.ResultInfo = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["md5"] = r.Md5, ["sha256"] = r.Sha256, ["size"] = r.Size,
                    ["apk_cert_sha256"] = (object?)r.ApkCertSha256!, ["apk_official"] = (object?)r.ApkOfficial!,
                    ["messages"] = new System.Collections.Generic.List<string>(r.Messages),
                    ["removed"] = new System.Collections.Generic.List<string>(r.Removed),
                    ["summary"] = r.Summary(),
                };
            }
            _status.PhaseRatio = 1.0;
        }
        return ok ? 0 : 1;
    }

    private async Task CrossCheckVersion(ProductSpec product)
    {
        if (string.IsNullOrEmpty(product.VersionApi)) return;
        var info = await Products.FetchMumuVersionInfoAsync(product.VersionApi);
        if (info is not null && info.TryGetValue("version", out var v) && v is string vs && !string.IsNullOrEmpty(vs))
        {
            var comps = info.TryGetValue("components", out var c) ? c as string ?? "" : "";
            lock (_status) _status.RemoteVersion = "V" + vs;
            Utils.LogInfo($"官方版本 API 最新: V{vs}" + (comps.Length > 0 ? $" | 引擎组件: {comps}" : ""));
        }
        else
        {
            Utils.LogInfo("官方版本 API 暂不可达（不影响下载，地址来自动态解析）");
        }
    }

    private async Task<int> ShowInfo(ProductSpec product, Resolution res, string outputPath)
    {
        _status.Phase(Phases.Probe, "探测远端文件…");
        var engine = new DownloadEngine(url: res.Url, outputPath: outputPath, sink: _status, autoVerify: false);
        _engine = engine;
        Dictionary<string, object> info;
        try { info = await engine.ProbeAsync(); }
        catch (Exception e)
        {
            _status.Phase(Phases.Failed, $"探测失败: {e.Message}");
            Utils.LogError($"探测文件信息失败: {e.Message}");
            return 1;
        }
        lock (_status)
        {
            _status.Total = (long)info["total_size"];
            _status.ResultInfo = new System.Collections.Generic.Dictionary<string, object>
            {
                ["final_url"] = info["url"], ["size"] = info["total_size"],
                ["accept_ranges"] = info["accept_ranges"], ["suggested_name"] = info["suggested_name"]!,
                ["canonical"] = Utils.CanonicalUrl((string)info["url"]),
            };
        }
        _status.Phase(Phases.Done, "信息已获取");
        Utils.LogInfo($"产品: {product.DisplayName}");
        Utils.LogInfo($"版本: {res.Version ?? "未知"} | 来源: {res.Brief()}");
        Utils.LogInfo($"最终 URL   : {info["url"]}");
        Utils.LogInfo($"文件大小   : {Utils.FormatSize((long)info["total_size"])}");
        Utils.LogInfo($"Range 支持 : {((bool)info["accept_ranges"] ? "是（可多线程+断点续传）" : "否（将回退单线程）")}");
        Utils.LogInfo($"建议文件名 : {info["suggested_name"] ?? "(未提供)"}");
        return 0;
    }

    private bool HandleExisting(string outputPath)
    {
        var cfg = _config;
        var stateFile = outputPath + ".dlstate.json";
        if (!(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0))
            return true;
        if (cfg.Force || File.Exists(stateFile))
            return true;       // 强制重下 / 有断点 -> 交给引擎处理

        var size = new FileInfo(outputPath).Length;
        var (ok, msg) = Verify.ChecksumRecord.Verify(outputPath);
        if (ok == true)
        {
            Utils.LogInfo($"目标文件已存在且基线校验通过: {outputPath} ({Utils.FormatSize(size)})");
            if (outputPath.ToLowerInvariant().EndsWith(".apk") && cfg.Verify)
                Verify.VerifyApkSignature(outputPath);
            return Confirm("是否仍要重新下载?", false);
        }
        if (ok == false)
        {
            Utils.LogError($"目标文件已存在但校验失败: {msg}");
            if (!Confirm("是否删除并重新下载?", true)) { _status.Error = "用户取消"; return false; }
            try { File.Delete(outputPath); Verify.ChecksumRecord.Remove(outputPath); }
            catch (IOException e) { Utils.LogError($"删除失败: {e.Message}"); return false; }
            return true;
        }

        Utils.LogInfo($"目标文件已存在: {outputPath} ({Utils.FormatSize(size)}) " +
                      $"（v3.0 校验通过后会清理基线记录，故无法离线复核）");
        if (outputPath.ToLowerInvariant().EndsWith(".apk") && cfg.Verify)
            Verify.VerifyApkSignature(outputPath);
        if (!Confirm("是否删除并重新下载?", false)) return false;
        try { File.Delete(outputPath); } catch (IOException e) { Utils.LogError($"删除失败: {e.Message}"); return false; }
        return true;
    }
}
