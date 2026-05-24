using System.IO;

namespace mmod_record.Services;

/// <summary>
/// 将运行日志写入程序运行目录下的 <c>logs</c> 文件夹（按日期滚动）。
/// </summary>
public static class PipelineLogger
{
    private static readonly object Lock = new();

    public static string LogDirectory =>
        Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            var file = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            lock (Lock)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不阻断管道
        }
    }
}
