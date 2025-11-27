using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
#if WINDOWS
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.Maui.Platform;
#endif

namespace systemcopyfile;

public partial class MainPage : ContentPage
{
    string? sourcePath;
    string? destinationPath;
    CancellationTokenSource? cts;

    public MainPage()
    {
        InitializeComponent();
        var raw = AppInfo.Current.VersionString;
        var parts = raw?.Split('.') ?? Array.Empty<string>();
        var ver = parts.Length == 4 && parts[3] == "0" ? $"{parts[0]}.{parts[1]}.{parts[2]}" : raw;
        HeaderLabel.Text = $"Sycf v{ver}";
#if WINDOWS
        EnsureDesktopShortcut();
#endif
    }

    async void OnSelectSourceClicked(object? sender, EventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrEmpty(path))
        {
            sourcePath = path;
            SourcePathEntry.Text = path;
            StatusLabel.Text = "源路径已选择";
        }
    }

    async void OnSelectDestinationClicked(object? sender, EventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrEmpty(path))
        {
            destinationPath = path;
            DestinationPathEntry.Text = path;
            StatusLabel.Text = "目标路径已选择";
        }
    }

    async void OnStartCopyClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            StatusLabel.Text = "请先选择源路径和目标路径";
            return;
        }
        var source = sourcePath!;
        var dest = destinationPath!;
        if (!EnsureDestinationWritable(dest, out var probeError))
        {
            StatusLabel.Text = probeError;
            return;
        }
        try
        {
            StartCopyButton.IsEnabled = false;
            CancelCopyButton.IsEnabled = true;
            CopyProgressBar.Progress = 0;
            ProgressTextLabel.Text = "0%";
            StatusLabel.Text = "复制进行中...";
            cts = new CancellationTokenSource();
            var progress = new Progress<double>(p =>
            {
                CopyProgressBar.Progress = p;
                ProgressTextLabel.Text = $"{p * 100:0.0}%";
            });
            var resume = ResumeSwitch?.IsToggled == true;
            var result = await Task.Run(() => CopyPath(source, dest, progress, cts.Token, resume));
            StatusLabel.Text = result;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"复制失败: {ex.Message}";
        }
        finally
        {
            StartCopyButton.IsEnabled = true;
            CancelCopyButton.IsEnabled = false;
            cts = null;
        }
    }

    void OnCancelClicked(object? sender, EventArgs e)
    {
        cts?.Cancel();
        StatusLabel.Text = "已取消";
    }

    string CopyPath(string source, string dest, IProgress<double> progress, CancellationToken token, bool resume)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(dest);
            var target = Path.Combine(dest, Path.GetFileName(source));
            try
            {
                var resumeOffset = resume ? GetResumeOffset(source, target) : 0;
                CopyFileWithProgress(source, target, progress, token, new FileInfo(source).Length, 0 + resumeOffset, resumeOffset);
                progress.Report(1);
                return "复制完成";
            }
            catch (Exception ex)
            {
                return $"复制失败: {ex.Message}";
            }
        }
        if (!Directory.Exists(source))
        {
            return "源路径不存在";
        }
        var total = GetDirectorySize(source);
        var copied = 0L;
        var errors = new List<string>();
        if (resume)
        {
            copied += GetInitialCopied(source, dest);
            if (total > 0) progress.Report((double)copied / total);
        }
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            var targetDir = Path.Combine(dest, rel);
            try { Directory.CreateDirectory(targetDir); } catch (Exception ex) { errors.Add(ex.Message); }
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, file);
            var targetFile = Path.Combine(dest, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                var resumeOffset = resume ? GetResumeOffset(file, targetFile) : 0;
                var srcLen = new FileInfo(file).Length;
                if (resume && resumeOffset >= srcLen)
                {
                    copied += srcLen;
                    if (total > 0) progress.Report((double)copied / total);
                    continue;
                }
                copied += CopyFileWithProgress(file, targetFile, progress, token, total, copied, resumeOffset);
            }
            catch (Exception ex)
            {
                errors.Add($"{rel}: {ex.Message}");
            }
        }
        progress.Report(1);
        if (errors.Count > 0)
        {
            try { WriteErrorsLog(dest, errors); } catch {}
            return $"完成(存在失败 {errors.Count})，详见 systemcopyfile_errors.log";
        }
        return "复制完成";
    }

    long GetDirectorySize(string path)
    {
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { size += new FileInfo(file).Length; } catch { }
        }
        return size;
    }

    long CopyFileWithProgress(string sourceFile, string destFile, IProgress<double> progress, CancellationToken token, long totalSize, long alreadyCopied, long resumeOffset)
    {
        var sPath = NormalizePath(sourceFile);
        var dPath = NormalizePath(destFile);
        try
        {
            using var src = new FileStream(sPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var destLen = File.Exists(dPath) ? new FileInfo(dPath).Length : 0L;
            if (resumeOffset > 0 && destLen >= resumeOffset)
            {
                using var dst = new FileStream(dPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1024 * 1024);
                if (destLen > src.Length) { dst.SetLength(0); resumeOffset = 0; }
                src.Position = resumeOffset;
                dst.Position = resumeOffset;
                var buffer = new byte[1024 * 1024];
                int read;
                long localCopied = 0;
                long lastReportedBytes = alreadyCopied;
                var sw = Stopwatch.StartNew();
                const long reportThresholdBytes = 4L * 1024 * 1024;
                const int reportThresholdMs = 150;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    localCopied += read;
                    var total = alreadyCopied + localCopied;
                    if (totalSize > 0)
                    {
                        if ((total - lastReportedBytes) >= reportThresholdBytes || sw.ElapsedMilliseconds >= reportThresholdMs)
                        {
                            progress.Report((double)total / totalSize);
                            lastReportedBytes = total;
                            sw.Restart();
                        }
                    }
                }
                dst.Flush(true);
                if (totalSize > 0) progress.Report((double)(alreadyCopied + localCopied) / totalSize);
                return resumeOffset + localCopied;
            }
            else
            {
                using var dst = new FileStream(dPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
                var buffer = new byte[1024 * 1024];
                int read;
                long localCopied = 0;
                long lastReportedBytes = alreadyCopied;
                var sw = Stopwatch.StartNew();
                const long reportThresholdBytes = 4L * 1024 * 1024;
                const int reportThresholdMs = 150;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    localCopied += read;
                    var total = alreadyCopied + localCopied;
                    if (totalSize > 0)
                    {
                        if ((total - lastReportedBytes) >= reportThresholdBytes || sw.ElapsedMilliseconds >= reportThresholdMs)
                        {
                            progress.Report((double)total / totalSize);
                            lastReportedBytes = total;
                            sw.Restart();
                        }
                    }
                }
                dst.Flush(true);
                if (totalSize > 0) progress.Report((double)(alreadyCopied + localCopied) / totalSize);
                return localCopied;
            }
        }
        catch
        {
            File.Copy(sPath, dPath, true);
            return new FileInfo(sPath).Length;
        }
    }

    bool EnsureDestinationWritable(string dest, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(dest);
            var testFile = Path.Combine(dest, ".systemcopyfile_write_test.tmp");
            File.WriteAllBytes(testFile, new byte[] { 1, 2, 3 });
            File.Delete(testFile);
            return true;
        }
        catch (Exception ex)
        {
            error = $"目标不可写: {ex.Message}";
            return false;
        }
    }

    string NormalizePath(string p)
    {
#if WINDOWS
        if (p.StartsWith("\\\\?\\")) return p;
        if (p.StartsWith("\\\\")) return "\\\\?\\UNC" + p.Substring(1);
        return "\\\\?\\" + p;
#else
        return p;
#endif
    }

    void WriteErrorsLog(string dest, List<string> errors)
    {
        var logPath = Path.Combine(dest, "systemcopyfile_errors.log");
        File.AppendAllLines(logPath, errors);
    }

#if WINDOWS
    void EnsureDesktopShortcut()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutPath = Path.Combine(desktop, $"systemcopyfile v{AppInfo.Current.VersionString}.lnk");
            if (File.Exists(shortcutPath)) return;
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "systemcopyfile", "systemcopyfile.exe");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic sc = shell.CreateShortcut(shortcutPath);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Save();
        }
        catch { }
    }
#endif

    long GetResumeOffset(string source, string target)
    {
        try
        {
            var sLen = new FileInfo(source).Length;
            if (!File.Exists(target)) return 0;
            var dLen = new FileInfo(target).Length;
            if (dLen <= 0) return 0;
            if (dLen >= sLen) return sLen;
            return dLen;
        }
        catch { return 0; }
    }

    long GetInitialCopied(string sourceRoot, string destRoot)
    {
        long sum = 0;
        foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(destRoot, rel);
            if (!File.Exists(target)) continue;
            try
            {
                var sLen = new FileInfo(file).Length;
                var dLen = new FileInfo(target).Length;
                sum += Math.Min(sLen, dLen);
            }
            catch { }
        }
        return sum;
    }

#if WINDOWS
    Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            var window = Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView as MauiWinUIWindow;
            if (window == null) return Task.FromResult<string?>(null);
            InitializeWithWindow.Initialize(picker, window.WindowHandle);
            return picker.PickSingleFolderAsync().AsTask().ContinueWith(t => t.Result?.Path);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }
#else
    Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
#endif
}
