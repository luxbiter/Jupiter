using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Jupiter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (TryHandleCommand(args))
        {
            return;
        }

        try
        {
            var launch = LaunchOptions.Resolve(args);
            using var server = new StaticFileServer(launch.RootDirectory);
            server.Start();

            var url = server.BuildUrl(launch.EntryPath);
            Application.Run(new BrowserForm(url, launch));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool TryHandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        if (command is "--install-context-menu" or "/install-context-menu")
        {
            ContextMenuInstaller.Install();
            if (!quiet)
            {
                MessageBox.Show("Jupiter context menu entries were installed.", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return true;
        }

        if (command is "--uninstall-context-menu" or "/uninstall-context-menu")
        {
            ContextMenuInstaller.Uninstall();
            if (!quiet)
            {
                MessageBox.Show("Jupiter context menu entries were removed.", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return true;
        }

        return false;
    }
}

internal sealed class BrowserForm : Form
{
    private readonly string _url;
    private readonly LaunchOptions _options;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private bool _isApplyingSize;

    public BrowserForm(string url, LaunchOptions options)
    {
        _url = url;
        _options = options;

        Text = options.Title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = options.Width;
        Height = options.Height;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        Controls.Add(_webView);

        if (options.Fullscreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }

        Load += async (_, _) => await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInfo.DisplayName,
            "WebView2Profile");
        Directory.CreateDirectory(userData);

        var environment = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection --autoplay-policy=no-user-gesture-required"
        });

        await _webView.EnsureCoreWebView2Async(environment);

        var settings = _webView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = _options.BrowserContextMenu;
        settings.AreDevToolsEnabled = _options.DevTools;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;

        _webView.CoreWebView2.WebMessageReceived += (_, e) => HandleWebMessage(e);
        _webView.CoreWebView2.DOMContentLoaded += async (_, _) => await FitWindowToPageAsync();
        _webView.CoreWebView2.NavigationCompleted += async (_, _) => await FitWindowToPageAsync();

        if (_options.HideScrollbars || _options.AutoResize)
        {
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PageSizing.Script);
        }

        _webView.CoreWebView2.Navigate(_url);
    }

    private void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_options.AutoResize)
        {
            return;
        }

        try
        {
            var size = JsonSerializer.Deserialize<PageSize>(e.WebMessageAsJson, JsonOptions.Default);
            if (size?.Type == PageSizing.MessageType)
            {
                BeginInvoke(() => ApplyPageSize(size.Width, size.Height));
            }
        }
        catch
        {
            // Ignore messages that did not come from Jupiter's sizing helper.
        }
    }

    private async Task FitWindowToPageAsync()
    {
        if (!_options.AutoResize || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var json = await _webView.CoreWebView2.ExecuteScriptAsync(PageSizing.MeasureScript);
            var size = JsonSerializer.Deserialize<PageSize>(json, JsonOptions.Default);
            if (size is not null)
            {
                ApplyPageSize(size.Width, size.Height);
            }
        }
        catch
        {
            // Pages can navigate or close while an async size read is in flight.
        }
    }

    private void ApplyPageSize(int contentWidth, int contentHeight)
    {
        if (_isApplyingSize || WindowState == FormWindowState.Minimized || _options.Fullscreen)
        {
            return;
        }

        _isApplyingSize = true;
        try
        {
            var workingArea = Screen.FromControl(this).WorkingArea;
            var borderWidth = Width - ClientSize.Width;
            var borderHeight = Height - ClientSize.Height;
            var maxClientWidth = Math.Max(320, Math.Min(_options.MaxWidth, workingArea.Width - borderWidth - 24));
            var maxClientHeight = Math.Max(240, Math.Min(_options.MaxHeight, workingArea.Height - borderHeight - 24));
            var naturalWidth = Math.Clamp(contentWidth, _options.MinWidth, _options.MaxWidth);
            var naturalHeight = Math.Clamp(contentHeight, _options.MinHeight, _options.MaxHeight);
            var zoom = Math.Min(1.0, Math.Min(maxClientWidth / (double)Math.Max(naturalWidth, 1), maxClientHeight / (double)Math.Max(naturalHeight, 1)));
            zoom = Math.Clamp(zoom, 0.25, 1.0);

            if (_webView.CoreWebView2 is not null && Math.Abs(_webView.ZoomFactor - zoom) > 0.01)
            {
                _webView.ZoomFactor = zoom;
            }

            var clientWidth = Math.Clamp((int)Math.Ceiling(naturalWidth * zoom), _options.MinWidth, maxClientWidth);
            var clientHeight = Math.Clamp((int)Math.Ceiling(naturalHeight * zoom), _options.MinHeight, maxClientHeight);
            ClientSize = new Size(clientWidth, clientHeight);
            CenterInsideWorkingArea(workingArea);
        }
        finally
        {
            _isApplyingSize = false;
        }
    }

    private void CenterInsideWorkingArea(Rectangle workingArea)
    {
        var x = Left;
        var y = Top;

        if (Right > workingArea.Right)
        {
            x = workingArea.Right - Width;
        }

        if (Bottom > workingArea.Bottom)
        {
            y = workingArea.Bottom - Height;
        }

        x = Math.Max(workingArea.Left, x);
        y = Math.Max(workingArea.Top, y);
        Location = new Point(x, y);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            ToggleFullscreen();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.I) && _options.DevTools)
        {
            _webView.CoreWebView2?.OpenDevToolsWindow();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleFullscreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            return;
        }

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
    }
}

internal sealed record LaunchOptions(
    string RootDirectory,
    string EntryPath,
    string Title,
    int Width,
    int Height,
    int MinWidth,
    int MinHeight,
    int MaxWidth,
    int MaxHeight,
    bool AutoResize,
    bool HideScrollbars,
    bool Fullscreen,
    bool DevTools,
    bool BrowserContextMenu)
{
    public static LaunchOptions Resolve(string[] args)
    {
        var exeDirectory = AppContext.BaseDirectory;
        var config = AppConfig.Load(Path.Combine(exeDirectory, "jupiter.json"))
            ?? AppConfig.Load(Path.Combine(exeDirectory, "webplay.json"))
            ?? AppConfig.Load(Path.Combine(Directory.GetCurrentDirectory(), "jupiter.json"))
            ?? AppConfig.Load(Path.Combine(Directory.GetCurrentDirectory(), "webplay.json"))
            ?? new AppConfig();

        var target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var configDirectory = config.SourceDirectory ?? exeDirectory;
        var root = ResolveConfigPath(config.GamePath, configDirectory)
            ?? FindDefaultGameDirectory(exeDirectory)
            ?? PickEntryFile();
        var entry = config.Entry ?? "index.html";

        if (File.Exists(root))
        {
            entry = Path.GetFileName(root);
            root = Path.GetDirectoryName(root) ?? Directory.GetCurrentDirectory();
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            var fullTarget = Path.GetFullPath(target);
            if (Directory.Exists(fullTarget))
            {
                root = fullTarget;
                entry = config.Entry ?? "index.html";
            }
            else if (File.Exists(fullTarget))
            {
                root = Path.GetDirectoryName(fullTarget) ?? Directory.GetCurrentDirectory();
                entry = Path.GetFileName(fullTarget);
            }
            else
            {
                throw new FileNotFoundException($"Target does not exist: {fullTarget}");
            }
        }

        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {root}");
        }

        var minWidth = Math.Max(config.MinWidth ?? 320, 160);
        var minHeight = Math.Max(config.MinHeight ?? 240, 120);
        var maxWidth = Math.Max(config.MaxWidth ?? 8192, minWidth);
        var maxHeight = Math.Max(config.MaxHeight ?? 8192, minHeight);

        return new LaunchOptions(
            root,
            NormalizeWebPath(entry),
            config.Title ?? AppInfo.DisplayName,
            Math.Max(config.Width ?? 1280, 320),
            Math.Max(config.Height ?? 720, 240),
            minWidth,
            minHeight,
            maxWidth,
            maxHeight,
            config.AutoResize ?? true,
            config.HideScrollbars ?? true,
            config.Fullscreen ?? false,
            config.DevTools ?? true,
            config.BrowserContextMenu ?? false);
    }

    private static string NormalizeWebPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string? ResolveConfigPath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path);
    }

    private static string? FindDefaultGameDirectory(string exeDirectory)
    {
        var candidates = new[]
        {
            exeDirectory,
            Directory.GetCurrentDirectory(),
            Path.Combine(exeDirectory, "game")
        };

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")));
    }

    private static string PickEntryFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a Jupiter game entry file",
            Filter = "Web game entry (*.html;*.htm)|*.html;*.htm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            throw new InvalidOperationException("No game was selected. Choose an index.html file or run Jupiter from a folder that contains one.");
        }

        return dialog.FileName;
    }
}

internal sealed class AppConfig
{
    [JsonIgnore]
    public string? SourceDirectory { get; set; }

    public string? GamePath { get; set; }
    public string? Entry { get; set; }
    public string? Title { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? MinWidth { get; set; }
    public int? MinHeight { get; set; }
    public int? MaxWidth { get; set; }
    public int? MaxHeight { get; set; }
    public bool? AutoResize { get; set; }
    public bool? HideScrollbars { get; set; }
    public bool? Fullscreen { get; set; }
    public bool? DevTools { get; set; }
    public bool? BrowserContextMenu { get; set; }

    public static AppConfig? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (config is not null)
        {
            config.SourceDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        }

        return config;
    }
}

internal sealed class PageSize
{
    public string? Type { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

internal static class PageSizing
{
    public const string MessageType = "jupiter:page-size";

    public const string MeasureScript = """
(() => {
  const doc = document.documentElement;
  const body = document.body;
  if (doc) doc.style.overflow = 'hidden';
  if (body) body.style.overflow = 'hidden';
  const width = Math.ceil(Math.max(
    doc?.scrollWidth || 0,
    doc?.offsetWidth || 0,
    doc?.clientWidth || 0,
    body?.scrollWidth || 0,
    body?.offsetWidth || 0
  ));
  const height = Math.ceil(Math.max(
    doc?.scrollHeight || 0,
    doc?.offsetHeight || 0,
    doc?.clientHeight || 0,
    body?.scrollHeight || 0,
    body?.offsetHeight || 0
  ));
  return { type: 'jupiter:page-size', width, height };
})()
""";

    public const string Script = """
(() => {
  if (window.__jupiterSizingInstalled) return;
  window.__jupiterSizingInstalled = true;

  const measure = () => {
    const doc = document.documentElement;
    const body = document.body;
    if (doc) doc.style.overflow = 'hidden';
    if (body) body.style.overflow = 'hidden';
    const width = Math.ceil(Math.max(
      doc?.scrollWidth || 0,
      doc?.offsetWidth || 0,
      doc?.clientWidth || 0,
      body?.scrollWidth || 0,
      body?.offsetWidth || 0
    ));
    const height = Math.ceil(Math.max(
      doc?.scrollHeight || 0,
      doc?.offsetHeight || 0,
      doc?.clientHeight || 0,
      body?.scrollHeight || 0,
      body?.offsetHeight || 0
    ));
    window.chrome?.webview?.postMessage({ type: 'jupiter:page-size', width, height });
  };

  let frame = 0;
  const schedule = () => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(measure);
  };

  addEventListener('load', schedule);
  addEventListener('resize', schedule);
  new MutationObserver(schedule).observe(document.documentElement, { attributes: true, childList: true, subtree: true });
  new ResizeObserver(schedule).observe(document.documentElement);
  setTimeout(schedule, 50);
  setTimeout(schedule, 250);
  setTimeout(schedule, 1000);
})()
""";
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class StaticFileServer : IDisposable
{
    private readonly string _rootDirectory;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _serverTask;

    public StaticFileServer(string rootDirectory)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        Port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public int Port { get; }
    public string BaseUrl { get; }

    public void Start()
    {
        _listener.Start();
        _serverTask = Task.Run(ListenAsync);
    }

    public string BuildUrl(string entryPath)
    {
        var encoded = string.Join("/", entryPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return new Uri(new Uri(BaseUrl), encoded).ToString();
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            AddCommonHeaders(context.Response);
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            var requestPath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath.TrimStart('/') ?? "");
            if (string.IsNullOrWhiteSpace(requestPath))
            {
                requestPath = "index.html";
            }

            var filePath = Path.GetFullPath(Path.Combine(_rootDirectory, requestPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInsideRoot(filePath))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            if (Directory.Exists(filePath))
            {
                filePath = Path.Combine(filePath, "index.html");
            }

            if (!File.Exists(filePath))
            {
                var fallback = Path.Combine(_rootDirectory, "index.html");
                if (File.Exists(fallback))
                {
                    filePath = fallback;
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
            }

            var content = MimeTypes.GetContent(filePath);
            context.Response.ContentType = content.Type;
            if (content.Encoding is not null)
            {
                context.Response.Headers["Content-Encoding"] = content.Encoding;
            }

            context.Response.ContentLength64 = new FileInfo(filePath).Length;
            if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await using var file = File.OpenRead(filePath);
            await file.CopyToAsync(context.Response.OutputStream);
        }
        catch
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static void AddCommonHeaders(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, max-age=0";
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    }

    private bool IsInsideRoot(string path)
    {
        var root = _rootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _stop.Dispose();

        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Shutdown path only.
        }
    }
}

internal static class MimeTypes
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".data"] = "application/octet-stream",
        [".bundle"] = "application/octet-stream",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2"
    };

    public static (string Type, string? Encoding) GetContent(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".br", StringComparison.OrdinalIgnoreCase))
        {
            var withoutCompression = Path.GetFileNameWithoutExtension(filePath);
            var innerExtension = Path.GetExtension(withoutCompression);
            var encoding = extension.Equals(".gz", StringComparison.OrdinalIgnoreCase) ? "gzip" : "br";
            return (Get(innerExtension), encoding);
        }

        return (Get(extension), null);
    }

    public static string Get(string extension)
    {
        return Types.TryGetValue(extension, out var type) ? type : "application/octet-stream";
    }
}

internal static class ContextMenuInstaller
{
    private static readonly string ExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Jupiter.exe");

    public static void Install()
    {
        Uninstall();
        SetCommand(@"Software\Classes\Directory\shell\Jupiter", "Open with Jupiter", $"\"{ExePath}\" \"%1\"");
        SetCommand(@"Software\Classes\Directory\Background\shell\Jupiter", "Open folder with Jupiter", $"\"{ExePath}\" \"%V\"");
        SetCommand(@"Software\Classes\htmlfile\shell\Jupiter", "Run with Jupiter", $"\"{ExePath}\" \"%1\"");
        SetCommand(@"Software\Classes\SystemFileAssociations\.html\shell\Jupiter", "Run with Jupiter", $"\"{ExePath}\" \"%1\"");
        SetCommand(@"Software\Classes\SystemFileAssociations\.htm\shell\Jupiter", "Run with Jupiter", $"\"{ExePath}\" \"%1\"");
        SetCommand(@"Software\Classes\.html\shell\Jupiter", "Run with Jupiter", $"\"{ExePath}\" \"%1\"");
        SetCommand(@"Software\Classes\.htm\shell\Jupiter", "Run with Jupiter", $"\"{ExePath}\" \"%1\"");
        RefreshExplorer();
    }

    public static void Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\WebPlay", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\WebPlay", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\htmlfile\shell\WebPlay", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.html\shell\WebPlay", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.htm\shell\WebPlay", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\Jupiter", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\Jupiter", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\htmlfile\shell\Jupiter", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.html\shell\Jupiter", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\.htm\shell\Jupiter", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.html\shell\Jupiter", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.htm\shell\Jupiter", false);
        RefreshExplorer();
    }

    private static void SetCommand(string keyPath, string label, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("", label);
        key.SetValue("MUIVerb", label);
        key.SetValue("Icon", ExePath);
        key.SetValue("Position", "Top");

        using var commandKey = key.CreateSubKey("command");
        commandKey.SetValue("", command);
    }

    private static void RefreshExplorer()
    {
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}

internal static class AppInfo
{
    public const string DisplayName = "Jupiter";
}
