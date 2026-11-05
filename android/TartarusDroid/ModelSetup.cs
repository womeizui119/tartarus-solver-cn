using Android.Content;

namespace TartarusDroid;

/// <summary>
/// 模型文件随安装包（assets/models），第一次启动或换版本后解压到 App 自己的数据目录，
/// 再用 TARTARUS_HOME 告诉识别和推算库去那里找（必须在用到这些库之前设好）。
/// </summary>
public static class ModelSetup
{
    private static readonly object Gate = new();

    public static string Prepare(Context context)
    {
        lock (Gate)
        {
            string home = Path.Combine(context.FilesDir!.AbsolutePath, "tartarus");
            string stamp = Path.Combine(home, "models.version");
            string version = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.LongVersionCode + "-" + typeof(ModelSetup).Assembly.ManifestModule.ModuleVersionId;
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != version)
            {
                string models = Path.Combine(home, "models");
                if (Directory.Exists(models)) Directory.Delete(models, recursive: true);
                Copy(context.Assets!, "models", models);
                File.WriteAllText(stamp, version);
            }
            Environment.SetEnvironmentVariable("TARTARUS_HOME", home);
            return home;
        }
    }

    private static void Copy(Android.Content.Res.AssetManager assets, string from, string to)
    {
        string[] children = assets.List(from) ?? [];
        if (children.Length == 0)
        {
            // 没有子项的就是文件
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using var input = assets.Open(from);
            using var output = File.Create(to);
            input.CopyTo(output);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (string child in children) Copy(assets, from + "/" + child, Path.Combine(to, child));
    }
}
