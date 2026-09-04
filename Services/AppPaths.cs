using System;
using System.IO;

namespace myrouter.Services;

/// <summary>
/// 运行期数据目录：exe 同目录下的 .myrouter/，集中存放配置/记忆等非程序本体文件，
/// 便于整体拷贝迁移，也避免散落在 exe 旁边。
/// </summary>
public static class AppPaths
{
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, ".myrouter");

    public static string ConfigFile => Path.Combine(DataDir, "myrouter.config.json");
    public static string MemoryFile => Path.Combine(DataDir, "memory.json");
    public static string CompanionFile => Path.Combine(DataDir, "companion.json");

    public static void EnsureDir() => Directory.CreateDirectory(DataDir);

    /// <summary>
    /// 一次性迁移：把旧版散落在 exe 同目录的配置文件挪进 .myrouter/。
    /// 幂等（目标已存在则跳过）；失败不阻塞启动。
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            foreach (var (legacyName, target) in new[]
            {
                ("myrouter.config.json", ConfigFile),
                ("memory.json", MemoryFile),
                ("companion.json", CompanionFile),
            })
            {
                var src = Path.Combine(AppContext.BaseDirectory, legacyName);
                if (File.Exists(src) && !File.Exists(target))
                {
                    EnsureDir();
                    File.Move(src, target);
                }
            }
        }
        catch { /* 迁移失败不阻塞启动，下次启动重试 */ }
    }
}