using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Security.Cryptography;
using Note.Services;

namespace Note
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "text/plain")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "text/html")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "application/msword")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "content", DataMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "text/plain")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "text/html")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "application/msword")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "file", DataMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            TintSystemBars();
            HandleIncomingIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleIncomingIntent(intent);
        }

        private void TintSystemBars()
        {
            if (Window is null) return;
            var bg = Android.Graphics.Color.ParseColor("#F4F5F7");
            Window.SetStatusBarColor(bg);
            Window.SetNavigationBarColor(bg);
            var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(Window, Window.DecorView);
            controller.AppearanceLightStatusBars = true;
            controller.AppearanceLightNavigationBars = true;
        }

        private void HandleIncomingIntent(Intent? intent)
        {
            var uri = intent?.Data;
            if (uri is null || intent?.Action != Intent.ActionView) return;

            // file:// 直接读原文件，不再复制一份到应用缓存
            if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase)
                && uri.Path is { } local && File.Exists(local))
            {
                AppState.SetPendingFile(local, Path.GetFileName(local));
                return;
            }

            _ = Task.Run(() => HandleContentUri(uri));
        }

        /// <summary>content:// 只能复制，但用内容哈希命名，同一文档永远只留一份。</summary>
        private void HandleContentUri(Android.Net.Uri uri)
        {
            var tmp = string.Empty;
            try
            {
                var name = ResolveFileName(uri);
                var dir = Path.Combine(FileSystem.CacheDirectory, "shared");
                Directory.CreateDirectory(dir);
                tmp = Path.Combine(dir, "tmp_" + Guid.NewGuid().ToString("N"));

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];

                using (var input = ContentResolver?.OpenInputStream(uri))
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (input is null) throw new InvalidOperationException("无法读取该文件");
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        output.Write(buffer, 0, n);
                    }
                }

                var id = Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
                var target = Path.Combine(dir, "c_" + id + Path.GetExtension(name).ToLowerInvariant());

                if (File.Exists(target)) File.Delete(tmp);
                else File.Move(tmp, target);
                tmp = string.Empty;

                DocumentReader.RememberFingerprint(target, id);
                AppState.SetPendingFile(target, name);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开外部文件失败: {ex}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tmp))
                {
                    try { File.Delete(tmp); }
                    catch { /* ignore */ }
                }
            }
        }

        private string ResolveFileName(Android.Net.Uri uri)
        {
            string? name = null;
            try
            {
                using var cursor = ContentResolver?.Query(uri, null, null, null, null);
                if (cursor is not null && cursor.MoveToFirst())
                {
                    var index = cursor.GetColumnIndex(Android.Provider.IOpenableColumns.DisplayName);
                    if (index >= 0) name = cursor.GetString(index);
                }
            }
            catch
            {
                // 忽略，退回用路径推断
            }

            if (string.IsNullOrWhiteSpace(name))
                name = uri.LastPathSegment is { } seg ? Path.GetFileName(seg) : null;

            if (string.IsNullOrWhiteSpace(name))
                name = $"导入_{DateTime.Now:yyyyMMddHHmmss}.txt";

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }
    }
}
