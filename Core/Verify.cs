namespace NeteaseGameDownloader.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeteaseGameDownloader.Models;

/// <summary>
/// 完整性与官方性校验（对应 Python 的 verify.py）。
/// 1. MD5/SHA256 单遍计算 + 无用 checksum 文件清理；
/// 2. APK 签名块（v2/v3）解析，比对网易官方证书指纹。
/// </summary>
public static partial class Verify
{
    private const string ChecksumSuffix = ".checksum.json";
    private static readonly HashSet<string> NeteaseCertSha256 =
    [
        "ae09c8d3c1be22476807ff0b79fa5985fde94c01dd831afed548feebd55cb731",
    ];

    private const string TrustedEnv = "EGGLD_TRUSTED_CERTS_FILE";
    private static readonly Regex TrustedRe = _trustedRe();

    // 缓存 JsonSerializerOptions，避免每次序列化都新建实例（CA1869）。
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public static string TrustedConfigPath()
    {
        var p = Environment.GetEnvironmentVariable(TrustedEnv);
        if (!string.IsNullOrEmpty(p)) return Path.GetFullPath(p);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "eggy-dl", "known_apk_certs.json");
    }

    public static HashSet<string> LoadTrustedFingerprints(string? configPath = null)
    {
        var result = new HashSet<string>(NeteaseCertSha256, StringComparer.OrdinalIgnoreCase);
        var path = configPath ?? TrustedConfigPath();
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("fingerprints", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String &&
                            TrustedRe.IsMatch(item.GetString()!))
                            result.Add(item.GetString()!.ToLowerInvariant());
                    }
                }
            }
        }
        catch (Exception) { /* 损坏则静默忽略 */ }
        return result;
    }

    public static string TrustFingerprint(string fingerprint, string? configPath = null)
    {
        var fp = (fingerprint ?? "").Trim().ToLowerInvariant();
        if (!TrustedRe.IsMatch(fp))
            throw new ArgumentException("指纹必须是 64 位十六进制 SHA256 字符串");
        var path = configPath ?? TrustedConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existing = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("fingerprints", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String) existing.Add(item.GetString()!);
                }
            }
        }
        catch (Exception) { /* ignore */ }

        var fps = existing.Select(e => e.ToLowerInvariant()).ToHashSet();
        fps.Add(fp);
        var data = new
        {
            fingerprints = fps.OrderBy(x => x).ToList(),
            note = "通过信任机制添加的 APK 签名证书 SHA256 指纹白名单",
            updated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".certs-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, IndentedOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
        return path;
    }

    public static bool IsKnownNeteaseCert(string certSha256)
    {
        var fp = (certSha256 ?? "").ToLowerInvariant();
        if (NeteaseCertSha256.Contains(fp)) return true;
        return LoadTrustedFingerprints().Contains(fp);
    }

    // ---------------------------------------------------------------------
    // APK Signing Block 解析
    // ---------------------------------------------------------------------
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("APK Sig Block 42");
    private const uint PairIdV2 = 0x7109871A;
    private const uint PairIdV3 = 0xF05368C0;

    public sealed class ApkCertInfo
    {
        public byte[] CertDer { get; set; } = [];
        public string CertSha256 { get; set; } = "";
        public string CertMd5 { get; set; } = "";
        public string Scheme { get; set; } = "";
        public List<string> OrgHints { get; set; } = [];
    }

    public class ApkSignatureException(string msg) : Exception(msg)
    {
    }

    public static ApkCertInfo ParseApkSigningCert(string path)
    {
        var fileSize = new FileInfo(path).Length;
        if (fileSize < 22 + 24)
            throw new ApkSignatureException("文件过小，不是有效的 APK");

        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        // 1. 读尾部定位 EOCD
        var tailLen = (int)Math.Min(fileSize, 22 + 0xFFFF + 1024);
        f.Seek(fileSize - tailLen, SeekOrigin.Begin);
        var tail = new byte[tailLen];
        ReadExact(f, tail, tailLen);
        var eocdAbs = FindEocd(tail, fileSize - tailLen, fileSize);
        if (eocdAbs < 0) throw new ApkSignatureException("未找到 ZIP EOCD（文件不是有效的 APK/ZIP）");

        // 2. 中央目录偏移 -> 签名块紧邻其前
        var cdOff = CdOffset(f, eocdAbs);
        if (cdOff < 24) throw new ApkSignatureException("中央目录偏移异常，未找到签名块");
        f.Seek(cdOff - 16, SeekOrigin.Begin);
        var magic = new byte[16];
        ReadExact(f, magic, 16);
        if (!magic.SequenceEqual(Magic))
            throw new ApkSignatureException("未找到 APK Signing Block（可能仅 v1 签名或未签名）");
        f.Seek(cdOff - 24, SeekOrigin.Begin);
        var blockSizeBuf = new byte[8];
        ReadExact(f, blockSizeBuf, 8);
        var blockSize = BitConverter.ToUInt64(blockSizeBuf, 0);
        var blockStart = cdOff - 8 - (long)blockSize;
        if (blockStart < 0 || blockSize > 100L * 1024 * 1024)
            throw new ApkSignatureException("APK Signing Block 尺寸异常");

        // 3. 读取 ID-value 对
        f.Seek(blockStart + 8, SeekOrigin.Begin);
        var pairsLen = (int)(blockSize - 24);
        if (pairsLen <= 0) throw new ApkSignatureException("签名块内容为空");
        var pairsBuf = new byte[pairsLen];
        ReadExact(f, pairsBuf, pairsLen);
        var pos = 0;
        byte[]? certDer = null;
        string? scheme = null;
        while (pos + 12 <= pairsLen)
        {
            var pairLen = (long)BitConverter.ToUInt64(pairsBuf, pos);
            if (pairLen < 4 || pos + 8 + pairLen > pairsLen) break;
            var pairId = BitConverter.ToUInt32(pairsBuf, pos + 8);
            var value = new byte[pairLen];
            Array.Copy(pairsBuf, pos + 12, value, 0, (int)pairLen);
            if (pairId is PairIdV2 or PairIdV3)
            {
                try
                {
                    certDer = ExtractCert(value);
                    scheme = pairId == PairIdV2 ? "v2" : "v3";
                    break;
                }
                catch (ApkSignatureException)
                {
                    if (scheme is null) throw;
                }
            }
            pos += 8 + (int)pairLen;
        }
        if (certDer is null) throw new ApkSignatureException("签名块中未找到 v2/v3 签名数据");

        var hints = ExtractPrintable(certDer);
        return new ApkCertInfo
        {
            CertDer = certDer,
            CertSha256 = Hex(SHA256.HashData(certDer)),
            CertMd5 = Hex(MD5.HashData(certDer)),
            Scheme = scheme!,
            OrgHints = hints,
        };
    }

    private static void ReadExact(FileStream f, byte[] buf, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = f.Read(buf, read, count - read);
            if (n == 0) throw new ApkSignatureException("读取 APK 失败（文件截断）");
            read += n;
        }
    }

    private static long CdOffset(FileStream f, long eocdAbs)
    {
        f.Seek(eocdAbs + 16, SeekOrigin.Begin);
        var b = new byte[4];
        ReadExact(f, b, 4);
        var cdOff = BitConverter.ToUInt32(b, 0);
        if (cdOff == 0xFFFFFFFF) // ZIP64
        {
            f.Seek(eocdAbs - 20, SeekOrigin.Begin);
            var locator = new byte[20];
            ReadExact(f, locator, 20);
            if (locator.Length < 20 || !locator.Take(4).SequenceEqual(new byte[] { 0x50, 0x4b, 0x06, 0x07 }))
                throw new ApkSignatureException("ZIP64 locator 缺失");
            var z64 = BitConverter.ToUInt64(locator, 8);
            f.Seek((long)z64, SeekOrigin.Begin);
            var header = new byte[56];
            ReadExact(f, header, 56);
            if (header.Length < 56 || !header.Take(4).SequenceEqual(new byte[] { 0x50, 0x4b, 0x06, 0x06 }))
                throw new ApkSignatureException("ZIP64 EOCD 无效");
            return (long)BitConverter.ToUInt64(header, 48);
        }
        return cdOff;
    }

    private static int FindEocd(byte[] tail, long tailStart, long fileSize)
    {
        // PK\x05\x06
        var sig = new byte[] { 0x50, 0x4b, 0x05, 0x06 };
        for (var idx = tail.Length - 22; idx >= 0; idx--)
        {
            if (tail[idx] == sig[0] && tail[idx + 1] == sig[1] &&
                tail[idx + 2] == sig[2] && tail[idx + 3] == sig[3])
            {
                var commentLen = tail[idx + 20] | (tail[idx + 21] << 8);
                if (tailStart + idx + 22 + commentLen == fileSize)
                    return (int)(tailStart + idx);
            }
        }
        return -1;
    }

    private static byte[] ExtractCert(byte[] value)
    {
        var (signers, _) = ReadLp(value, 0);
        var (signer, _) = ReadLp(signers, 0);
        var (signedData, off) = ReadLp(signer, 0);
        var (_, off2) = ReadLp(signedData, 0); // digests
        var (certs, _) = ReadLp(signedData, off2);
        var (cert, _) = ReadLp(certs, 0);
        if (cert.Length == 0) throw new ApkSignatureException("证书数据为空");
        return cert;
    }

    private static (byte[] data, int next) ReadLp(byte[] buf, int off)
    {
        if (off + 4 > buf.Length) throw new ApkSignatureException("签名数据截断（长度前缀越界）");
        var n = (int)BitConverter.ToUInt32(buf, off);
        var end = off + 4 + n;
        if (end > buf.Length) throw new ApkSignatureException("签名数据截断（数据体越界）");
        var data = new byte[n];
        Array.Copy(buf, off + 4, data, 0, n);
        return (data, end);
    }

    private static List<string> ExtractPrintable(byte[] der, int minLen = 4)
    {
        var found = new List<string>();
        var start = -1;
        var sb = new StringBuilder();
        for (var i = 0; i < der.Length; i++)
        {
            var c = der[i];
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                     (c >= '0' && c <= '9') || c == (byte)' ' || c == (byte)'.' ||
                     c == (byte)'_' || c == (byte)'@' || c == (byte)'-';
            if (ok)
            {
                if (start < 0) start = i;
                sb.Append((char)c);
            }
            else if (start >= 0)
            {
                if (sb.Length >= minLen && !found.Contains(sb.ToString())) found.Add(sb.ToString());
                start = -1; sb.Clear();
            }
        }
        if (start >= 0 && sb.Length >= minLen && !found.Contains(sb.ToString())) found.Add(sb.ToString());
        return [.. found.Take(8)];
    }

    public static ApkCertInfo? VerifyApkSignature(string path)
    {
        ApkCertInfo info;
        try { info = ParseApkSigningCert(path); }
        catch (ApkSignatureException e)
        {
            Utils.LogWarn($"APK 签名解析失败: {e.Message}");
            return null;
        }
        var org = info.OrgHints.FirstOrDefault(s => s.Contains("NETEASE", StringComparison.InvariantCultureIgnoreCase));
        if (IsKnownNeteaseCert(info.CertSha256))
        {
            Utils.LogSuccess($"APK 签名验证通过（{info.Scheme}，网易官方证书" +
                             (org is not null ? $"，O={org}" : "") + "）");
            Utils.LogInfo($"证书 SHA256 指纹: {info.CertSha256}");
        }
        else
        {
            Utils.LogWarn($"APK 签名证书不在内置的网易官方指纹列表中（{info.Scheme}）!");
            Utils.LogWarn($"证书 SHA256 指纹: {info.CertSha256}");
            Utils.LogWarn("文件可能被二次打包，请谨慎安装。");
            Utils.LogWarn($"如确认官方更换过证书，可信任该指纹");
        }
        return info;
    }

    // ---------------------------------------------------------------------
    // 基线校验记录
    // ---------------------------------------------------------------------
    public sealed class ChecksumRecord
    {
        public string Md5 { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string? Url { get; set; }
        public string? Created { get; set; }
        public string? ApkCertSha256 { get; set; }

        public static (string md5, string sha256) ComputeHashes(string path, Action<long>? onProgress = null, int block = 1024 * 1024)
        {
            using var md5 = MD5.Create();
            using var sha = SHA256.Create();
            long acc = 0;
            var buf = new byte[block];
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, block, FileOptions.SequentialScan);
            int read;
            while ((read = f.Read(buf, 0, block)) > 0)
            {
                md5.TransformBlock(buf, 0, read, null, 0);
                sha.TransformBlock(buf, 0, read, null, 0);
                acc += read;
                onProgress?.Invoke(acc);
            }
            md5.TransformFinalBlock([], 0, 0);
            sha.TransformFinalBlock([], 0, 0);
            return (Hex(md5.Hash!), Hex(sha.Hash!));
        }

        public static ChecksumRecord Create(string target, string? url = null, string? apkCert = null)
        {
            var (md5, sha) = ComputeHashes(target);
            var rec = new ChecksumRecord
            {
                Md5 = md5,
                Sha256 = sha,
                Size = new FileInfo(target).Length,
                Url = url,
                Created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ApkCertSha256 = apkCert,
            };
            rec.Save(target);
            return rec;
        }

        public void Save(string target)
        {
            var path = Path.GetFullPath(target) + ChecksumSuffix;
            var data = new
            {
                md5 = Md5, sha256 = Sha256, size = Size,
                url = Url, created = Created, apk_cert_sha256 = ApkCertSha256,
            };
            var dir = Path.GetDirectoryName(path) ?? ".";
            var tmp = Path.Combine(dir, $".checksum-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, IndentedOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception)
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
                throw;
            }
        }

        public static ChecksumRecord? Load(string target)
        {
            var path = Path.GetFullPath(target) + ChecksumSuffix;
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                return new ChecksumRecord
                {
                    Md5 = r.GetProperty("md5").GetString()!,
                    Sha256 = r.GetProperty("sha256").GetString()!,
                    Size = r.GetProperty("size").GetInt64(),
                    Url = r.TryGetProperty("url", out var u) ? u.GetString() : null,
                    Created = r.TryGetProperty("created", out var c) ? c.GetString() : null,
                    ApkCertSha256 = r.TryGetProperty("apk_cert_sha256", out var a) ? a.GetString() : null,
                };
            }
            catch (Exception) { return null; }
        }

        public static void Remove(string target)
        {
            try { File.Delete(Path.GetFullPath(target) + ChecksumSuffix); } catch (IOException) { /* ignore */ }
        }

        public static (bool? ok, string msg) Verify(string target)
        {
            var rec = Load(target);
            if (rec is null) return (null, "无校验记录");
            if (!File.Exists(target)) return (false, "文件不存在");
            var actual = new FileInfo(target).Length;
            if (actual != rec.Size) return (false, $"大小不匹配（记录 {rec.Size}，实际 {actual}）");
            var (md5, sha) = ComputeHashes(target);
            if (md5 != rec.Md5 || sha != rec.Sha256)
                return (false, "哈希不匹配（文件已损坏或被篡改）");
            return (true, "MD5/SHA256 与记录一致");
        }
    }

    private static readonly string[] ChecksumSuffixes =
    [
        ChecksumSuffix, ".checksum", ".checksums", ".md5", ".sha1", ".sha256", ".sha512", ".sfv",
    ];

    public static List<string> FindChecksumFiles(string target)
    {
        var basePath = Path.GetFullPath(target);
        var found = new List<string>();
        foreach (var suffix in ChecksumSuffixes)
        {
            var candidate = basePath + suffix;
            if (File.Exists(candidate) && !found.Contains(candidate)) found.Add(candidate);
        }
        var dir = Path.GetDirectoryName(basePath) ?? ".";
        var stem = Path.GetFileName(basePath);
        try
        {
            foreach (var name in Directory.GetFiles(dir))
            {
                if (!File.Exists(name)) continue;
                var low = name.ToLowerInvariant();
                var fn = Path.GetFileName(name);
                if (fn != stem && fn.StartsWith(stem, StringComparison.InvariantCultureIgnoreCase) &&
                    ChecksumSuffixes.Any(s => low.EndsWith(s)) && !found.Contains(name))
                    found.Add(name);
                else if (fn.StartsWith(".checksum-", StringComparison.InvariantCultureIgnoreCase) && fn.ToLowerInvariant().EndsWith(".tmp")
                         && !found.Contains(name))
                    found.Add(name);
            }
        }
        catch (IOException) { /* ignore */ }
        return found;
    }

    public static List<string> CleanupChecksumFiles(string target, bool keep = false)
    {
        var removed = new List<string>();
        var basePath = Path.GetFullPath(target);
        foreach (var path in FindChecksumFiles(target))
        {
            if (keep && path == basePath + ChecksumSuffix) continue;
            if (keep && !path.EndsWith(".tmp")) continue;
            try { File.Delete(path); removed.Add(Path.GetFileName(path)); }
            catch (IOException) { /* ignore */ }
        }
        return removed;
    }

    public static VerifyResult VerifyNow(string target, string? expectedMd5 = null,
                                        bool doApk = true, bool keepChecksum = false,
                                        Action<double>? onProgress = null, string? recordUrl = null)
    {
        var res = new VerifyResult { ExpectedMd5 = (expectedMd5 ?? "").ToLowerInvariant() is var m && m.Length > 0 ? m : null };
        if (!File.Exists(target)) { res.Messages.Add("文件不存在，无法校验"); return res; }

        res.Size = new FileInfo(target).Length;
        var total = res.Size == 0 ? 1 : res.Size;

        void Cb(long read) => onProgress?.Invoke(Math.Min(1.0, read / (double)total));
        var (md5, sha) = ChecksumRecord.ComputeHashes(target, Cb);
        res.Md5 = md5;
        res.Sha256 = sha;

        if (res.ExpectedMd5 is not null)
        {
            res.Md5Match = md5 == res.ExpectedMd5;
            if (res.Md5Match.Value)
                res.Messages.Add($"MD5 与官方值一致: {md5}");
            else
            {
                res.Ok = false;
                res.Messages.Add($"MD5 校验失败! 期望 {res.ExpectedMd5}，实际 {md5}");
                res.Messages.Add("文件可能已损坏或被篡改，请强制重新下载");
                return res;
            }
        }
        else
        {
            res.Messages.Add("已计算 MD5/SHA256（官方下载页未提供校验值，作为本次下载基线）");
        }

        if (doApk && target.ToLowerInvariant().EndsWith(".apk"))
        {
            try
            {
                var info = ParseApkSigningCert(target);
                res.ApkCertSha256 = info.CertSha256;
                res.ApkOfficial = IsKnownNeteaseCert(info.CertSha256);
                if (res.ApkOfficial.Value)
                    res.Messages.Add($"APK 签名验证通过（{info.Scheme}，网易官方证书）");
                else
                    res.Messages.Add($"APK 签名证书不在内置网易官方指纹列表（{info.Scheme}），" +
                                     $"指纹 {info.CertSha256}；如确认官方换过证书可执行信任操作");
            }
            catch (ApkSignatureException e)
            {
                res.ApkOfficial = false;
                res.Messages.Add($"APK 签名解析失败: {e.Message}");
            }
        }

        res.Ok = true;

        if (keepChecksum)
        {
            try
            {
                var rec = new ChecksumRecord
                {
                    Md5 = md5,
                    Sha256 = sha,
                    Size = res.Size,
                    Url = recordUrl,
                    ApkCertSha256 = res.ApkCertSha256,
                };
                rec.Save(target);
                res.Messages.Add($"基线校验记录已保留: {Path.GetFileName(target)}{ChecksumSuffix}");
            }
            catch (Exception e) { res.Messages.Add($"基线记录保存失败: {e.Message}"); }
        }

        res.Removed = CleanupChecksumFiles(target, keep: keepChecksum);
        if (res.Removed.Count > 0)
            res.Messages.Add("已清理无用校验文件: " + string.Join(", ", res.Removed));
        return res;
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.IgnoreCase, "zh-CN")]
    private static partial Regex _trustedRe();
}
