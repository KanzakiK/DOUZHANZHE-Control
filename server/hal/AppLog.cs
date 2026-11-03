// SPDX-License-Identifier: MIT
//
// AppLog — 统一日志基础设施
// ============================
// 职责：
//   所有层（DriverBridge / HAL / SMU / NVAPI / CpuPower / API）共用一个日志文件。
//   2MB 自动轮转，保留最近 3 个文件。
//   日志路径: %LOCALAPPDATA%/Douzhanzhe Console/logs/app.log

using System;
using System.IO;

namespace Douzhanzhe.HAL;

public static class AppLog
{
    private static readonly object _lock = new();
    private static string _logDir = "";
    private static string _logName = "app";
    private static string _logFile = "";
    private static long _size;
    private static volatile bool _initialized;
    private const long MaxSize = 2 * 1024 * 1024;  // 2MB 轮转
    private const int KeepFiles = 3;                // 保留最近 3 个文件

    /// <summary>
    /// 初始化日志目录。必须在所有 AppLog.Write 之前调用。
    /// 建议位置: Program.cs 启动流程最前面（服务注册之前）。
    /// </summary>
    /// <param name="logDir">日志目录</param>
    /// <param name="fileSuffix">日志文件名后缀。正式安装版传空（app.log）；
    /// 开发实例传 "-dev"（app-dev.log）—— 开发/正式必须分文件写，否则两边日志互相覆盖。</param>
    public static void Init(string logDir, string fileSuffix = "")
    {
        lock (_lock)
        {
            _logDir = logDir;
            Directory.CreateDirectory(logDir);
            _logName = $"app{fileSuffix}";
            _logFile = Path.Combine(logDir, $"{_logName}.log");
            _size = File.Exists(_logFile) ? new FileInfo(_logFile).Length : 0;
            _initialized = true;
        }
    }

    /// <summary>
    /// 写入一行日志。Init 之前调用会静默降级到 Console.WriteLine。
    /// </summary>
    public static void Write(string tag, string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {msg}\n";

        if (!_initialized)
        {
            // Init 之前降级到控制台，避免丢失关键启动日志
            Console.Write(line);
            return;
        }

        lock (_lock)
        {
            try
            {
                if (_size > MaxSize) Rotate();
                // ★ 必须 FileShare.ReadWrite：File.AppendAllText 用的是 FileShare.Read，
                //   会排斥其它进程写入 —— 安装版实例在写日志时，第二个实例（开发实例/维护助手）
                //   的日志会被静默吞掉并降级到控制台，导致"日志凭空消失"。
                using (var fs = new FileStream(_logFile, FileMode.Append, FileAccess.Write,
                                                FileShare.ReadWrite | FileShare.Delete))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(line);
                }
                _size += System.Text.Encoding.UTF8.GetByteCount(line);
            }
            catch
            {
                // 日志写入失败不能影响业务，降级到控制台
                Console.Write(line);
            }
        }
    }

    /// <summary>日志目录路径（Init 后可用）</summary>
    public static string LogDir => _logDir;

    private static void Rotate()
    {
        try
        {
            for (int i = KeepFiles - 1; i > 0; i--)
            {
                var src = Path.Combine(_logDir, $"{_logName}.log.{i}");
                var dst = Path.Combine(_logDir, $"{_logName}.log.{i + 1}");
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            File.Move(_logFile, Path.Combine(_logDir, $"{_logName}.log.1"), overwrite: true);
            _size = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AppLog] Rotate failed: {ex.Message}");
        }
    }
}
