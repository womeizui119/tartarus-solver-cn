using Android.Content;
using Android.Graphics;
using Android.Provider;
using Tartarus.Vision;

namespace TartarusDroid;

/// <summary>
/// 存图：保存到手机的“下载/TartarusSolver”文件夹（文件管理里就能找到），同一文件夹里的 记录.txt 写着每张图当时认出了什么。
/// 手动存 PNG（清楚），自动存 JPG（省空间，约 0.5MB 一张）。
/// </summary>
public static class Saver
{
    private const string Folder = "Download/TartarusSolver/";
    private static readonly object Gate = new();
    private static Android.Net.Uri? _logUri;

    public static string SaveFrame(Context ctx, Frame frame, bool png, string note)
    {
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + (png ? ".png" : ".jpg");
        using var bitmap = Bitmap.CreateBitmap(frame.Width, frame.Height, Bitmap.Config.Argb8888!)!;
        bitmap.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(frame.Pixels));
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        values.Put(MediaStore.IMediaColumns.MimeType, png ? "image/png" : "image/jpeg");
        values.Put(MediaStore.IMediaColumns.RelativePath, Folder);
        var uri = ctx.ContentResolver!.Insert(MediaStore.Downloads.ExternalContentUri!, values);
        if (uri == null) return name;
        using (var output = ctx.ContentResolver.OpenOutputStream(uri)!)
            bitmap.Compress(png ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!, png ? 100 : 90, output);
        Log(ctx, $"{name}\t{note}");
        return name;
    }

    public static void Log(Context ctx, string line)
    {
        lock (Gate)
        {
            try
            {
                if (_logUri == null)
                {
                    var values = new ContentValues();
                    values.Put(MediaStore.IMediaColumns.DisplayName, "记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    values.Put(MediaStore.IMediaColumns.MimeType, "text/plain");
                    values.Put(MediaStore.IMediaColumns.RelativePath, Folder);
                    _logUri = ctx.ContentResolver!.Insert(MediaStore.Downloads.ExternalContentUri!, values);
                }
                if (_logUri == null) return;
                using var output = ctx.ContentResolver!.OpenOutputStream(_logUri, "wa")!;
                var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                output.Write(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
                // 记录写不进去不影响存图
            }
        }
    }
}
