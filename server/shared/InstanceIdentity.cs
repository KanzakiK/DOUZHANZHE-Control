// SPDX-License-Identifier: MIT
//
// InstanceIdentity — 区分"正式安装版"与"开发实例"
// ===================================================
// 正式版由安装器注册（可在任意盘符），开发实例跑在仓库目录（server/*/bin/...）。
// 两者会争抢同一套硬件（EC/SMU 写、风扇控制、PawnIO）与 WMI 服务，必须互斥；
// 日志也必须分开写，否则互相覆盖。
//
// 本文件被两个项目以链接方式共用（不引入新的项目依赖）：
//   server/api/Douzhanzhe.API.csproj
//   server/shell/Douzhanzhe.Shell/Douzhanzhe.Shell.csproj

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Douzhanzhe.Shared;

internal static class InstanceIdentity
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E7D5C2B1-8F3A-4E2D-9B6C-1A2F3E4D5C6B}_is1";

    /// <summary>是否是"正式安装版"（当前目录属于安装器注册目录或默认 Program Files）。</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                return IsInstalledPath(AppContext.BaseDirectory);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 日志文件后缀：正式版为空（app.log），开发版为 "-dev"（app-dev.log）。
    /// 可用环境变量 DOUZHANZHE_LOG_SUFFIX 覆盖（多开发实例时区分）。
    /// </summary>
    public static string LogSuffix
    {
        get
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("DOUZHANZHE_LOG_SUFFIX");
                if (!string.IsNullOrWhiteSpace(env))
                    return env.StartsWith("-") ? env : "-" + env.Trim();
            }
            catch { }
            return IsInstalled ? "" : "-dev";
        }
    }

    /// <summary>日志文件全路径（按实例隔离）。</summary>
    public static string LogFilePath(string logDir) => Path.Combine(logDir, $"app{LogSuffix}.log");

    /// <summary>判断某个可执行文件路径是否属于"正式安装版"。</summary>
    public static bool IsInstalledPath(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        try
        {
            var candidate = Path.GetFullPath(exePath);
            foreach (var root in InstalledRoots())
            {
                if (IsUnderRoot(candidate, root)) return true;
            }
        }
        catch { }
        return false;
    }

    private static IEnumerable<string> InstalledRoots()
    {
        // Inno 允许安装到任意盘符（本机即 D:\Program Files），仅比较
        // Environment.SpecialFolder.ProgramFiles 会把正式版误判成开发版。
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? installLocation = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(UninstallKey);
                    installLocation = key?.GetValue("InstallLocation") as string;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(installLocation))
                    yield return installLocation;
            }
        }

        // 安装注册表暂时不可用时仍识别默认 Program Files 安装。
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Douzhanzhe Console");
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "Douzhanzhe Console");
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCandidate = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(
                    normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>指定端口是否已被占用（TCP 连接探测，不 spawn 子进程）。</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var task = tcp.ConnectAsync("127.0.0.1", port);
            return task.Wait(500) && tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// 取占用指定端口的进程 exe 路径（取不到返回 null）。
    /// 用 netstat 精确定位"谁在监听这个端口"，用于判断占用者是不是正式安装目录下的那个 API。
    /// </summary>
    public static string? GetPortOwnerExePath(int port)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port}") || !line.Contains("LISTENING")) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (!int.TryParse(parts[^1], out var pid)) continue;
                try { return System.Diagnostics.Process.GetProcessById(pid).MainModule?.FileName; }
                catch { return null; }
            }
        }
        catch { }
        return null;
    }
}
