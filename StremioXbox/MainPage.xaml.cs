using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using StremioXbox.Core;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace StremioXbox
{
    /// <summary>
    /// Hosts Stremio Web in WebView2, plays handed-over streams in the native Xbox player,
    /// and shows an on-screen debug log (View button).
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private const string StremioUrl = "https://web.stremio.com/";

        // On a PC (testing without an Xbox) the keyboard drives the app too; on Xbox only the controller does.
        private static readonly bool IsXbox = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
        private const int VisibleLogLines = 40;

        // MF_MT_TRANSFER_FUNCTION: 15 = SMPTE ST 2084 (PQ / HDR10), 16 = HLG.
        private static readonly Guid TransferFunctionKey = new Guid("5fb0fce9-be5c-4935-a811-ec838f8eed93");

        private readonly List<string> _log = new List<string>();
        private readonly DisplayModeManager _displayModes = new DisplayModeManager();
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private WebView2 _webView;
        private MediaPlayer _mediaPlayer;
        private PlayRequest _current;
        private bool _nativePlayerEnabled = true;
        private bool _displayRequestActive;
        private DispatcherTimer _diagnosticsTimer;
        private long _fileSize; // From the probe; 0 until known.
        private bool _bitrateLogged;
        private Stopwatch _paceClock;
        private TimeSpan _lastPosition;
        private double _lastDownload;
        private int _paceTicks;
        private readonly GamepadInput _gamepad = new GamepadInput();
        private RemoteControl _remote;
        private DispatcherTimer _osdTimer;
        private DateTime _osdHideAt;
        private bool _cursorMode = ReadSetting("cursorMode", false);
        private bool _attachSubtitles = true; // LB in the debug log toggles it (stutter tests in 0.1.3.1).
        private HttpChunkCache _cache;
        private int _connections = 8; // 0 = hand the URL to the player directly; RB in the debug log cycles it.

        // Restart seek modes (Menu in the debug log cycles them): seconds to jump forward before returning;
        // positive = after playback starts, negative = before playing (0.1.3.7), 0 = off.
        private static readonly int[] RestartSeekModes = { 10, 30, 60, -60, 0 };
        private int _restartSeekMode;

        // Page zoom as real browser zoom (Left in the debug log cycles it, saved). Before a reinstall the page
        // sat at ~70% from a zoom level WebView2 had stored in the app data; at 100% the side menu runs off
        // screen. CSS zoom only shrank the 1920x1080 layout into a corner, so the app instead tells Chromium
        // the screen is 1920/zoom x 1080/zoom CSS px at zoom device pixels (DevTools Emulation), which is
        // what browser zoom does.
        private static readonly double[] ZoomSteps = { 0.6, 0.67, 0.7, 0.75, 0.8, 0.9, 1.0 };
        private double _pageZoom = ReadSetting("pageZoom", 1.0); // 100% by default (user decision); Left in the debug log tries others.
        private readonly bool _systemCursor = ReadSetting("systemCursor", false); // As set at this launch.
        private bool _systemCursorNext = ReadSetting("systemCursor", false); // Applies at the next launch.
        private bool _keyboardVisible; // Xbox on-screen keyboard is up: the app leaves the controller to it.

        // The app's own cursor, in page DIPs (the Xbox system cursor never engaged on the WebView2).
        private double _cursorX = 960;
        private double _cursorY = 540;
        private int _lastCursorMove;
        private double _pendingScroll;
        private int _lastBackTick = Environment.TickCount - 1000;
        private bool _pageAtHome = true; // From shell.js route messages.
        private long _lastCacheBytes;

        // More connections when the download falls behind the file's bitrate (see AdaptConnections).
        private int _slowChecks;
        private double _rateBeforeAdding;
        private bool _connectionsMaxed;
        private string _osdMessage;

        // Startup / connection screen.
        private bool _pageShown;         // The first successful load hid the startup screen.
        private bool _statusRetry;       // The connection error is up; A retries.
        private CoreWebView2ProcessFailedKind? _pendingRecovery; // The page crashed during playback; recover when it ends.
        private DispatcherTimer _toastTimer;

        // Player loading screen and the Stremio session it belongs to (see shell.js).
        private bool _preparing;         // Loading screen up, waiting for 'play' from the page.
        private double _preparingSession;
        private double _cancelledSession = -1;
        private DateTime _preparingSince;
        private volatile bool _starting; // Media opened, resuming / prebuffering before the first Play.
        private string _contentType;     // Of the buffered source, to open it again for an audio switch.

        // Audio switch to another format: the same file is opened again (from the RAM buffer) with the new
        // track chosen before playback starts, then returns to where it was. Switching formats live crashed
        // the player (E-AC3 -> AC3); within one format the switch stays live.
        private bool _reopening;
        private TimeSpan _reopenPosition;
        private bool _reopenWasPaused;
        private int _reopenFallback = -1; // Track to go back to if the new one fails to open.
        private string _restoreSubtitle;  // Subtitle shown before the reopen ("" = off, null = nothing to do).
        private volatile string _audioNote; // Set while choosing the first audio track, shown on the bar.
        private bool _hasPlayed;         // Play was called: from here on the position is worth saving.
        private DispatcherTimer _progressTimer;
        private bool? _reportedPaused;
        private int _lastProgressTick;
        private string _nextLabel;       // Next episode, from Stremio; null when there is none.
        private bool _binge = true;
        private bool _upNextDismissed;
        private bool _subtitleUserChoice;
        private bool _subtitleAutoDone;

        // External (addon) subtitles by label, with their language: a downloaded subtitle file has no language of
        // its own. Preferred over subtitles inside the video file: with one of those on, the Xbox player reads
        // ahead through the file for the next line, and on a sparse track (lines only for some scenes) it ran
        // hundreds of MB ahead at the download edge, holding up the picture (2 s of video per 5 s).
        private readonly Dictionary<string, string> _externalSubtitleLanguages = new Dictionary<string, string>();
        private int _externalSubtitlesPending; // Added but not loaded (or failed) yet.
        private bool _embeddedSubtitleAllowed; // No downloaded subtitle in the language came in time.
        private bool _subtitleWaiting;
        private int _subtitleGeneration;
        private volatile bool _audioDecided;  // The first audio choice is made (subtitles wait for it).
        private volatile bool _originalAudio; // A foreign title plays in its own language: English subtitles on.
        private string _audioLanguage;        // The first audio choice's language ("en"; null when unknown).
        private bool _startOverRequested;     // X on the loading screen: start at 0:00 instead of resuming.
        private TaskCompletionSource<bool> _seekAbort; // Ends the wait of a running SeekAndWaitAsync early.

        // D-pad / shoulder seeking: presses add up to a target that is applied once they stop.
        private TimeSpan? _scrubTarget;
        private int _scrubRepeats;
        private int _lastScrubTick;
        private DispatcherTimer _scrubTimer;

        // Audio & Subtitles panel: two columns (audio, subtitles), each with its own highlighted row.
        private readonly List<MenuEntry>[] _menuColumns = new List<MenuEntry>[2];
        private readonly int[] _menuIndexes = new int[2];
        private int _menuColumn;

        // Stremio's intro timing for the current video, when it knows it.
        private TimeSpan? _introFrom;
        private TimeSpan? _introTo;
        private bool _skipIntroDismissed;

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SetUpFades();

            // The Skip intro / Up Next cards sit low in the corner and move up above the bar while it shows.
            Osd.RegisterPropertyChangedCallback(VisibilityProperty, (sender, property) =>
            {
                if (Osd.Visibility != Visibility.Visible)
                {
                    SetCenterIndicator(null);
                }

                PositionCards();
            });
            Osd.SizeChanged += (sender, e) => PositionCards();
            PlayerLayer.SizeChanged += (sender, e) => UpdateLetterbox();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += OnAcceleratorKeyActivated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _gamepad.Pressed += OnPadButton;
            _gamepad.Polled += OnPadPolled;
            _gamepad.Start();
            WatchKeyboard();
            ApplyCursorMode();
            Application.Current.EnteredBackground += OnEnteredBackground;
            Application.Current.LeavingBackground += OnLeavingBackground;
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;

            var version = Package.Current.Id.Version;
            Log("info", string.Format("Stremio-XBOX {0}.{1}.{2}", version.Major, version.Minor, version.Build));
            Log("info", DisplayModeManager.DescribeDisplay());
            UpdateDebugHeader();

            _remote = new RemoteControl(HandleRemoteAsync, LogAsync);
            _ = _remote.StartAsync();

            await InitializeWebViewAsync();
        }

        // ---- Remote control (scripts\Xbox-Remote.ps1; Developer Mode installs only, see RemoteControl) ----

        private static readonly Dictionary<string, PadButton> RemoteButtons = new Dictionary<string, PadButton>(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = PadButton.Up, ["down"] = PadButton.Down, ["left"] = PadButton.Left, ["right"] = PadButton.Right,
            ["a"] = PadButton.A, ["x"] = PadButton.X, ["y"] = PadButton.Y, ["view"] = PadButton.View, ["menu"] = PadButton.Menu,
            ["start"] = PadButton.Menu, ["lb"] = PadButton.LeftShoulder, ["rb"] = PadButton.RightShoulder,
            ["lt"] = PadButton.LeftTrigger, ["rt"] = PadButton.RightTrigger, ["ls"] = PadButton.LeftStick,
        };

        // Runs each request on the UI thread, as a controller press or a look at the screen would.
        private Task<string> HandleRemoteAsync(RemoteRequest request)
        {
            var done = new TaskCompletionSource<string>();
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    done.TrySetResult(await HandleRemoteOnUiAsync(request));
                }
                catch (Exception ex)
                {
                    done.TrySetResult("{\"error\":" + RemoteControl.Json(ex.Message) + "}");
                }
            });
            return done.Task;
        }

        private async Task<string> HandleRemoteOnUiAsync(RemoteRequest request)
        {
            var path = request.Path.TrimEnd('/');
            if (path == "/ping" || path == string.Empty)
            {
                var version = Package.Current.Id.Version;
                return "{\"app\":\"Stremio-XBOX\",\"version\":\"" + version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision + "\"}";
            }

            if (path.StartsWith("/press/", StringComparison.Ordinal))
            {
                var name = path.Substring("/press/".Length);
                Log("info", "Remote: press " + name);
                if (name == "b")
                {
                    var handled = HandleBack();
                    return "{\"pressed\":\"b\",\"handled\":" + (handled ? "true" : "false") + "}";
                }

                if (!RemoteButtons.TryGetValue(name, out var button))
                {
                    return "{\"error\":" + RemoteControl.Json("unknown button: " + name) + "}";
                }

                OnPadButton(button);
                return "{\"pressed\":" + RemoteControl.Json(name) + "}";
            }

            if (path == "/state")
            {
                return await RemoteStateAsync();
            }

            if (path == "/seek")
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null || _starting || !double.TryParse(request.Get("to") ?? request.Body, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    return "{\"error\":\"needs a playing video and ?to=<seconds>\"}";
                }

                Log("info", "Remote: seek to " + FormatTime(TimeSpan.FromSeconds(seconds)));
                var ok = await SeekAndWaitAsync(session, TimeSpan.FromSeconds(Math.Max(0, seconds)), true);
                return "{\"seeked\":" + (ok ? "true" : "false") + ",\"position\":" + RemoteControl.Json(FormatTime(session.Position)) + "}";
            }

            if (path == "/log")
            {
                var count = int.TryParse(request.Get("lines"), out var n) ? Math.Max(1, Math.Min(n, _log.Count)) : Math.Min(100, _log.Count);
                var lines = _log.Skip(Math.Max(0, _log.Count - count)).Select(RemoteControl.Json);
                return "{\"lines\":[" + string.Join(",", lines) + "]}";
            }

            if (path == "/navigate" || path == "/eval")
            {
                if (_webView?.CoreWebView2 == null)
                {
                    return "{\"error\":\"the page is not ready\"}";
                }

                var script = path == "/navigate"
                    ? "location.hash = " + RemoteControl.Json(request.Body.Trim().StartsWith("#") ? request.Body.Trim() : "#" + request.Body.Trim()) + "; location.hash"
                    : request.Body;
                Log("info", "Remote: " + path.Substring(1) + " " + (script.Length > 80 ? script.Substring(0, 80) + "..." : script).Replace("\n", " "));
                var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);
                return "{\"result\":" + (string.IsNullOrEmpty(result) ? "null" : result) + "}";
            }

            return "{\"error\":\"unknown command; try /ping, /state, /press/<button>, /seek?to=<s>, /navigate, /eval, /log\"}";
        }

        private async Task<string> RemoteStateAsync()
        {
            var parts = new List<string>
            {
                "\"player\":" + (PlayerLayer.Visibility == Visibility.Visible ? "true" : "false"),
                "\"loading\":" + (LoadingPanel.Visibility == Visibility.Visible
                    ? "{\"title\":" + RemoteControl.Json(LoadingTitle.Text) + ",\"text\":" + RemoteControl.Json(LoadingText.Text) + ",\"keys\":" + RemoteControl.Json(LoadingKeys.Text) + "}"
                    : "null"),
                "\"tracksPanel\":" + (TrackMenu.Visibility == Visibility.Visible ? "true" : "false"),
                "\"debugLog\":" + (DebugPanel.Visibility == Visibility.Visible ? "true" : "false"),
                "\"toast\":" + (Toast.Visibility == Visibility.Visible ? RemoteControl.Json(ToastText.Text) : "null"),
                "\"osdMessage\":" + RemoteControl.Json(_osdMessage),
            };

            var session = _mediaPlayer?.PlaybackSession;
            if (session != null)
            {
                try
                {
                    var item = _mediaPlayer.Source as MediaPlaybackItem;
                    string audio = null;
                    string subtitle = null;
                    if (item != null)
                    {
                        var audioIndex = item.AudioTracks.SelectedIndex;
                        audio = audioIndex >= 0 && audioIndex < item.AudioTracks.Count ? DescribeAudio(item.AudioTracks[audioIndex]) : null;
                        for (var i = 0; i < item.TimedMetadataTracks.Count; i++)
                        {
                            if (IsSubtitleTrack(item.TimedMetadataTracks[i]) &&
                                item.TimedMetadataTracks.GetPresentationMode((uint)i) == TimedMetadataTrackPresentationMode.PlatformPresented)
                            {
                                subtitle = DescribeSubtitle(item.TimedMetadataTracks[i]);
                                break;
                            }
                        }
                    }

                    parts.Add("\"playback\":{\"state\":" + RemoteControl.Json(session.PlaybackState.ToString()) +
                        ",\"position\":" + RemoteControl.Json(FormatTime(session.Position)) +
                        ",\"duration\":" + RemoteControl.Json(FormatTime(session.NaturalDuration)) +
                        ",\"title\":" + RemoteControl.Json(_current?.DisplayTitle) +
                        ",\"audio\":" + RemoteControl.Json(audio) +
                        ",\"subtitles\":" + RemoteControl.Json(subtitle) + "}");
                }
                catch (Exception ex)
                {
                    parts.Add("\"playback\":{\"error\":" + RemoteControl.Json(ex.Message) + "}");
                }
            }

            var page = "null";
            if (_webView?.CoreWebView2 != null)
            {
                try
                {
                    var result = await _webView.CoreWebView2.ExecuteScriptAsync("window.__sxState ? window.__sxState() : null");
                    page = string.IsNullOrEmpty(result) ? "null" : result;
                }
                catch (Exception ex)
                {
                    page = "{\"error\":" + RemoteControl.Json(ex.Message) + "}";
                }
            }

            parts.Add("\"page\":" + page);
            return "{" + string.Join(",", parts) + "}";
        }

        private async Task InitializeWebViewAsync()
        {
            // Let video start without a real user gesture: controller input reaches the page as
            // synthetic events, which Chromium's autoplay policy would otherwise ignore.
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
            _webView = _systemCursor ? new WebView2() : new WebView2 { RequiresPointer = RequiresPointer.Never };
            RootGrid.Children.Insert(0, _webView);
            _webView.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigationCompleted += OnNavigationCompleted;

            try
            {
                await _webView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                Log("error", "WebView2 failed to start: " + ex.Message);
                ShowStatus("Stremio couldn't start", "The Microsoft Edge WebView2 runtime failed to start (" + ex.Message + "). Restart the app; if it keeps happening, reinstall it with its Dependencies.", "View  Show log", false);
                return;
            }

            // A crashed page would otherwise leave a blank screen: reload it (after playback, if a video is on).
            _webView.CoreWebView2.ProcessFailed += (sender, args) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => OnWebProcessFailed(args.ProcessFailedKind));

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsGeneralAutofillEnabled = false; // Autofill pops the virtual keyboard on Xbox.

            StartRequestLogging();

            // Links that would open a new window launch Edge and push this app to the background.
            _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
            {
                args.Handled = true;
                Log("warn", "Blocked new window: " + args.Uri);
            };

            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Resources/shell.js"));
                var script = await FileIO.ReadTextAsync(file);
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }
            catch (Exception ex)
            {
                Log("error", "Could not inject shell script: " + ex.Message);
                SetDebugVisible(true);
            }

            _webView.Source = new Uri(StremioUrl);
            _webView.Focus(FocusState.Programmatic);
        }

        // Diagnostics for the missing home-screen catalogs (0.1.3.10): logs every request the page makes
        // to a host other than Stremio Web's own (addon manifests and catalogs), its status and time, and
        // requests that never get a response. Stremio's core fetches from a web worker, so shell.js cannot
        // see these; WebView2 can when the filter includes all source kinds.
        private const int LoggedRequests = 400;
        private readonly Dictionary<string, int> _pendingRequests = new Dictionary<string, int>();
        private int _requestLogs;
        private DispatcherTimer _requestTimer;

        private void StartRequestLogging()
        {
            _requestTimer?.Stop(); // The WebView2 may be recreated after a crash.
            var core = _webView.CoreWebView2;
            try
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                Log("info", "Request logging: all source kinds (page and workers)");
            }
            catch (Exception ex)
            {
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                Log("warn", "Request logging: page only (" + ex.GetType().Name + ")");
            }

            core.WebResourceRequested += (sender, args) =>
            {
                var uri = args.Request.Uri;
                if (!IsLoggedRequest(uri, args.ResourceContext))
                {
                    return;
                }

                lock (_pendingRequests)
                {
                    _pendingRequests[args.Request.Method + " " + uri] = Environment.TickCount;
                }

                LogRequest("info", string.Format("Request {0} {1} {2}", args.ResourceContext, args.Request.Method, DescribeUri(uri)));
            };

            core.WebResourceResponseReceived += (sender, args) =>
            {
                var uri = args.Request.Uri;
                int started;
                lock (_pendingRequests)
                {
                    var key = args.Request.Method + " " + uri;
                    if (!_pendingRequests.TryGetValue(key, out started))
                    {
                        return;
                    }

                    _pendingRequests.Remove(key);
                }

                var status = args.Response.StatusCode;
                LogRequest(status >= 200 && status < 400 ? "info" : "warn", string.Format("Response {0} {1} in {2} ms: {3}",
                    status, args.Response.ReasonPhrase, unchecked(Environment.TickCount - started), DescribeUri(uri)));
            };

            // Requests still unanswered after 15 s (failed DNS/TLS/CORS or hung) are reported once.
            _requestTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _requestTimer.Tick += (sender, e) =>
            {
                var now = Environment.TickCount;
                var stale = new List<KeyValuePair<string, int>>();
                lock (_pendingRequests)
                {
                    foreach (var pair in _pendingRequests)
                    {
                        if (unchecked(now - pair.Value) > 15000)
                        {
                            stale.Add(pair);
                        }
                    }

                    foreach (var pair in stale)
                    {
                        _pendingRequests.Remove(pair.Key);
                    }
                }

                foreach (var pair in stale)
                {
                    var space = pair.Key.IndexOf(' ');
                    LogRequest("warn", string.Format("No response after {0} s: {1} {2}", unchecked(now - pair.Value) / 1000,
                        pair.Key.Substring(0, space), DescribeUri(pair.Key.Substring(space + 1))));
                }
            };
            _requestTimer.Start();
        }

        private static bool IsLoggedRequest(string uri, CoreWebView2WebResourceContext context)
        {
            if (context == CoreWebView2WebResourceContext.Image || context == CoreWebView2WebResourceContext.Font ||
                context == CoreWebView2WebResourceContext.Stylesheet || context == CoreWebView2WebResourceContext.Media)
            {
                return false;
            }

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return false;
            }

            return !parsed.Host.EndsWith("web.stremio.com", StringComparison.OrdinalIgnoreCase);
        }

        // Host and path, with long path segments (addon configs, API keys) shortened.
        private static string DescribeUri(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return uri.Length > 120 ? uri.Substring(0, 120) + "..." : uri;
            }

            var segments = parsed.AbsolutePath.Split('/').Select(segment => segment.Length > 32 ? "<" + segment.Length + " chars>" : segment);
            var text = parsed.Host + string.Join("/", segments) + (parsed.Query.Length > 0 ? "?<query>" : string.Empty);
            return text.Length > 200 ? text.Substring(0, 200) + "..." : text;
        }

        private void LogRequest(string level, string text)
        {
            if (Interlocked.Increment(ref _requestLogs) <= LoggedRequests)
            {
                LogAsync(level, text);
            }
        }

        private static T ReadSetting<T>(string key, T fallback)
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[key] is T value ? value : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void SaveSetting(string key, object value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
            }
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task ApplyZoomAsync()
        {
            var core = _webView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            var width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Window.Current.Bounds.Width;
            var height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : Window.Current.Bounds.Height;
            try
            {
                if (_pageZoom >= 0.999)
                {
                    await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                }
                else
                {
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", string.Format(
                        "{{\"width\":{0},\"height\":{1},\"deviceScaleFactor\":{2},\"mobile\":false}}",
                        (int)Math.Round(width / _pageZoom), (int)Math.Round(height / _pageZoom), Num(_pageZoom)));
                }

                Log("info", string.Format("Page zoom {0}% (window {1}x{2})", Math.Round(_pageZoom * 100), Math.Round(width), Math.Round(height)));
            }
            catch (Exception ex)
            {
                Log("warn", "Page zoom failed: " + ex.Message);
            }
        }

        // ---- The app's own cursor -------------------------------------------------------------
        private void SetCursorMode(bool enabled)
        {
            _cursorMode = enabled;
            SaveSetting("cursorMode", enabled);
            ApplyCursorMode();
            Log("info", "Cursor " + (enabled ? "on (left stick moves, A clicks, right stick scrolls, stick click turns it off)" : "off"));
            if (!enabled)
            {
                MoveMouseAway();
            }

            UpdateDebugHeader();
        }

        // The page keeps a hover where the app's cursor last was, so after the cursor is turned off, Stremio's
        // white hover highlight stayed on that spot and moved onto whatever scrolled under it: a second
        // highlight next to the controller's. Moving the (virtual) mouse off the page clears it.
        private void MoveMouseAway()
        {
            var core = _webView?.CoreWebView2;
            if (core != null && !_systemCursor)
            {
                _ = SendDevToolsAsync(core, "Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\",\"x\":-1,\"y\":-1,\"button\":\"none\"}");
            }
        }

        private void ApplyCursorMode()
        {
            if (_systemCursor)
            {
                _cursorMode = false; // The Xbox cursor replaces the app's own.
            }

            _gamepad.StickIsCursor = _cursorMode || _systemCursor;
            CursorLayer.Visibility = _cursorMode && PlayerLayer.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            PlaceCursor();
        }

        private void PlaceCursor()
        {
            Canvas.SetLeft(CursorDot, _cursorX - (CursorDot.Width / 2));
            Canvas.SetTop(CursorDot, _cursorY - (CursorDot.Height / 2));
        }

        private void OnPadPolled(Windows.Gaming.Input.GamepadReading reading)
        {
            // Hidden while the native player is up; frozen while the debug log or a text dialog is open.
            var visible = _cursorMode && PlayerLayer.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (CursorLayer.Visibility != visible)
            {
                CursorLayer.Visibility = visible;
            }

            var active = visible == Visibility.Visible && !_keyboardVisible && DebugPanel.Visibility != Visibility.Visible;

            if (!active)
            {
                return;
            }

            // Left stick: speed grows with the square of the deflection (fine aim near the centre).
            var x = reading.LeftThumbstickX;
            var y = reading.LeftThumbstickY;
            var magnitude = Math.Sqrt((x * x) + (y * y));
            if (magnitude > 0.15)
            {
                var speed = 1500 * ((magnitude - 0.15) / 0.85) * ((magnitude - 0.15) / 0.85) / 60; // DIPs per poll at full tilt: 25
                var width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1920;
                var height = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : 1080;
                _cursorX = Math.Max(0, Math.Min(width - 1, _cursorX + (x / magnitude * speed)));
                _cursorY = Math.Max(0, Math.Min(height - 1, _cursorY - (y / magnitude * speed)));
                PlaceCursor();
                if (unchecked(Environment.TickCount - _lastCursorMove) > 33)
                {
                    _lastCursorMove = Environment.TickCount;
                    SendMouse("mouseMoved", "none", 0, 0);
                }
            }

            // Right stick: wheel scroll at the cursor.
            var scroll = reading.RightThumbstickY;
            if (Math.Abs(scroll) > 0.2)
            {
                _pendingScroll += -scroll * 30;
            }

            if (Math.Abs(_pendingScroll) >= 60)
            {
                SendMouse("mouseWheel", "none", 0, _pendingScroll);
                _pendingScroll = 0;
            }
        }

        private void ClickAtCursor()
        {
            SendMouse("mousePressed", "left", 1, 0);
            SendMouse("mouseReleased", "left", 1, 0);
        }

        // Trusted input through DevTools; coordinates are page CSS px (DIPs divided by the page zoom).
        private void SendMouse(string type, string button, int clickCount, double deltaY)
        {
            var core = _webView?.CoreWebView2;
            if (core == null)
            {
                return;
            }

            var json = string.Format(
                "{{\"type\":\"{0}\",\"x\":{1},\"y\":{2},\"button\":\"{3}\",\"clickCount\":{4},\"deltaX\":0,\"deltaY\":{5}}}",
                type, Num(_cursorX / _pageZoom), Num(_cursorY / _pageZoom), button, clickCount, Num(deltaY));
            _ = SendDevToolsAsync(core, "Input.dispatchMouseEvent", json);
        }

        private async Task SendDevToolsAsync(CoreWebView2 core, string method, string json)
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(method, json);
            }
            catch (Exception ex)
            {
                Log("warn", method + " failed: " + ex.Message);
            }
        }

        // ---- On-screen keyboard -------------------------------------------------------------------
        // A on a text field focuses it and WebView2 opens the Xbox keyboard (default behaviour). While the
        // keyboard is up the app must not act on the controller too, or the D-pad moved the page highlight
        // off the field and the typed text went nowhere (0.1.3.11 first test).
        private void WatchKeyboard()
        {
            try
            {
                var pane = Windows.UI.ViewManagement.InputPane.GetForCurrentView();
                pane.Showing += (sender, e) => SetKeyboardVisible(true, "InputPane");
                pane.Hiding += (sender, e) => SetKeyboardVisible(false, "InputPane");
            }
            catch (Exception ex)
            {
                Log("warn", "InputPane unavailable: " + ex.Message);
            }

            try
            {
                var view = Windows.UI.ViewManagement.Core.CoreInputView.GetForCurrentView();
                view.PrimaryViewShowing += (sender, e) => SetKeyboardVisible(true, "CoreInputView");
                view.PrimaryViewHiding += (sender, e) => SetKeyboardVisible(false, "CoreInputView");
            }
            catch (Exception ex)
            {
                Log("warn", "CoreInputView unavailable: " + ex.Message);
            }
        }

        // ---- Typing ---------------------------------------------------------------------------------
        // WebView2's Xbox keyboard delivered keys only right after focus had come back to the WebView2 from a
        // native text box (after the text dialog, and after the invisible typing box let go); after a plain
        // startup focus, the keyboard opened but no key arrived. So before every page text field, focus
        // passes through the hidden ProxyText and back, then the page focuses the field again.
        private bool _priming;
        private bool _typingReady; // One pass per page load was enough before (password after the dialog).

        private async Task PrimeTypingAsync(double fieldId, string reason, bool alwaysRefocus)
        {
            if (_priming || _webView == null)
            {
                return;
            }

            if (_typingReady)
            {
                if (alwaysRefocus)
                {
                    SendRefocusField(fieldId);
                }

                return;
            }

            _priming = true;
            try
            {
                // Focusable only during the pass: otherwise, when the page is hidden for the player, focus fell
                // to this box and A (pause) opened the keyboard.
                ProxyText.IsTabStop = true;
                var viaBox = ProxyText.Focus(FocusState.Keyboard);
                await Task.Delay(120);
                var back = _webView.Focus(FocusState.Keyboard);
                ProxyText.IsTabStop = false;
                await Task.Delay(80);
                _typingReady = viaBox && back && fieldId > 0;
                SendRefocusField(fieldId);
                Log("info", "Typing focus pass (" + reason + "): native box " + (viaBox ? "ok" : "FAILED") + ", WebView2 " + (back ? "ok" : "FAILED"));
            }
            catch (Exception ex)
            {
                Log("warn", "Typing focus pass failed: " + ex.Message);
            }
            finally
            {
                _priming = false;
            }
        }

        private void SendRefocusField(double fieldId)
        {
            if (fieldId <= 0)
            {
                return;
            }

            var message = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("refocusField"),
                ["id"] = JsonValue.CreateNumberValue(fieldId),
            };
            _webView?.CoreWebView2?.PostWebMessageAsString(message.Stringify());
        }

        private void SetKeyboardVisible(bool visible, string source)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_keyboardVisible != visible)
                {
                    _keyboardVisible = visible;
                    Log("info", "On-screen keyboard " + (visible ? "shown" : "hidden") + " (" + source + ")");
                }
            });
        }

        private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                Log("info", "Loaded " + sender.Source);
                SendConfigToPage();
                if (PlayerLayer.Visibility != Visibility.Visible)
                {
                    // At startup the Focus call runs before the page exists; focus it again once loaded.
                    _typingReady = false;
                    _ = PrimeTypingAsync(0, "page loaded", false);
                }
                _ = ApplyZoomAsync();
                _statusRetry = false;
                _pageShown = true;
                StatusLayer.Visibility = Visibility.Collapsed;
            }
            else if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                Log("info", "Navigation replaced before it finished");
            }
            else
            {
                Log("error", "Navigation failed: " + args.WebErrorStatus);
                ShowStatus("Can't reach Stremio", "Check that your Xbox is connected to the internet. (" + args.WebErrorStatus + ")", "A  Try again", false);
                _statusRetry = true;
            }
        }

        // ---- Startup / connection screen, toasts ----------------------------------------------------
        private void ShowStatus(string title, string text, string hint, bool busy)
        {
            StatusTitle.Text = title;
            StatusText.Text = text ?? string.Empty;
            StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            StatusHint.Text = hint ?? string.Empty;
            StatusHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
            StatusRing.IsActive = busy;
            StatusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusLayer.Visibility = Visibility.Visible;
        }

        private void RetryPage(string reason)
        {
            if (_webView?.CoreWebView2 == null)
            {
                return;
            }

            Log("info", "Reloading Stremio (" + reason + ")");
            _statusRetry = false;
            ShowStatus(_pageShown ? "Reloading Stremio" : "Loading Stremio", null, null, true);
            _typingReady = false;
            _webView.CoreWebView2.Navigate(StremioUrl);
        }

        private void OnNetworkStatusChanged(object sender)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                var online = profile != null && profile.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
                if (_statusRetry && online)
                {
                    RetryPage("back online");
                }
            });
        }

        private void OnWebProcessFailed(CoreWebView2ProcessFailedKind kind)
        {
            Log("error", "WebView2 process failed: " + kind);
            if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited &&
                kind != CoreWebView2ProcessFailedKind.RenderProcessExited &&
                kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                return; // A helper process (GPU, utility) restarts by itself.
            }

            if (PlayerLayer.Visibility == Visibility.Visible)
            {
                _pendingRecovery = kind; // Keep the video going; the page is hidden anyway.
                return;
            }

            _ = RecoverPageAsync(kind);
        }

        private async Task RecoverPageAsync(CoreWebView2ProcessFailedKind kind)
        {
            _pendingRecovery = null;
            if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                RetryPage("page crashed");
                return;
            }

            // The whole browser is gone: the control cannot be reused.
            ShowStatus("Reloading Stremio", null, null, true);
            var old = _webView;
            _webView = null;
            if (old != null)
            {
                RootGrid.Children.Remove(old);
                try
                {
                    old.Close();
                }
                catch (Exception ex)
                {
                    Log("warn", "Closing the old WebView2 failed: " + ex.Message);
                }
            }

            _typingReady = false;
            await InitializeWebViewAsync();
        }

        private void ShowToast(string text, double seconds = 6)
        {
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer();
                _toastTimer.Tick += (sender, e) =>
                {
                    _toastTimer.Stop();
                    Toast.Visibility = Visibility.Collapsed;
                };
            }

            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
            _toastTimer.Start();
        }

        private void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string raw;
            try
            {
                raw = args.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                return;
            }

            // Only our own messages have a string "type"; Stremio Web's desktop-shell IPC uses numbers.
            if (!JsonObject.TryParse(raw, out var message) ||
                !message.ContainsKey("type") ||
                message["type"].ValueType != JsonValueType.String)
            {
                return;
            }

            switch (message.GetNamedString("type", string.Empty))
            {
                case "log":
                    Log(message.GetNamedString("level", "info"), "web: " + message.GetNamedString("text", string.Empty));
                    break;
                case "preparing":
                    ShowPreparing(message.GetNamedNumber("session", 0), "Loading", message.GetNamedString("name", string.Empty).Replace("\n", " "));
                    break;
                case "play":
                    var request = PlayRequest.FromJson(message);
                    if (request.Session != 0 && request.Session == _cancelledSession)
                    {
                        Log("info", "Ignoring a stream that was cancelled while loading");
                        break;
                    }

                    StartNativePlayback(request);
                    break;
                case "nextInfo":
                    if (_current != null && message.GetNamedNumber("session", 0) == _current.Session)
                    {
                        SetNextLabel(message.GetNamedString("next", string.Empty));
                    }

                    break;
                case "introInfo":
                    if (_current != null && message.GetNamedNumber("session", 0) == _current.Session)
                    {
                        ApplyIntro(message.GetNamedNumber("introFrom", 0), message.GetNamedNumber("introTo", 0));
                        UpdateSkipIntro();
                    }

                    break;
                case "playerIdle":
                    // The page did not start another video (no next episode, or it opened its stream list).
                    if (_preparing && _mediaPlayer == null)
                    {
                        _preparing = false;
                        StopPlayback(resetDisplay: true);
                    }

                    break;
                case "textFocus":
                    _ = PrimeTypingAsync(message.GetNamedNumber("id", 0), "field focused by page", false);
                    break;
                case "typingStart":
                    _ = PrimeTypingAsync(message.GetNamedNumber("id", 0), "A on field", true);
                    break;
                case "route":
                    _pageAtHome = message.GetNamedBoolean("home", true);
                    break;
            }
        }

        private void SendConfigToPage()
        {
            var config = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("config"),
                ["nativePlayer"] = JsonValue.CreateBooleanValue(_nativePlayerEnabled),
                ["keyboardNav"] = JsonValue.CreateBooleanValue(!IsXbox),
            };

            try
            {
                _webView?.CoreWebView2?.PostWebMessageAsString(config.Stringify());
            }
            catch (Exception ex)
            {
                Log("error", "Could not send config to page: " + ex.Message);
            }
        }

        private void StartNativePlayback(PlayRequest request)
        {
            if (!_nativePlayerEnabled)
            {
                return;
            }

            StopPlayback(resetDisplay: false);
            _current = request;
            Log("info", "Native play: " + request);
            Log("info", "Stream URL: " + request.Url);
            _fileSize = 0;
            _bitrateLogged = false;
            _slowChecks = 0;
            _rateBeforeAdding = 0;
            _connectionsMaxed = false;
            _preparing = false;
            _starting = true;
            _hasPlayed = false;
            Curtain.Visibility = Visibility.Collapsed; // A playback closed during its restart seek leaves it up.
            Letterbox.Visibility = Visibility.Collapsed;
            _reportedPaused = null;
            _upNextDismissed = false;
            _skipIntroDismissed = false;
            ApplyIntro(request.IntroFromMs, request.IntroToMs);
            _subtitleUserChoice = false;
            _subtitleAutoDone = false;
            _embeddedSubtitleAllowed = false;
            _subtitleWaiting = false;
            _subtitleGeneration++;
            _audioDecided = false;
            _originalAudio = false;
            _audioLanguage = null;
            _reopening = false;
            _reopenFallback = -1;
            _restoreSubtitle = null;
            _audioNote = null;
            _contentType = null;
            _binge = request.Binge;
            _nextLabel = string.IsNullOrEmpty(request.Next) ? null : request.Next;
            LoadingTitle.Text = request.DisplayTitle;
            var resumes = request.StartPositionMs > 0 && !_startOverRequested;
            LoadingText.Text = resumes
                ? "Resuming from " + FormatTime(TimeSpan.FromMilliseconds(request.StartPositionMs))
                : request.StartPositionMs > 0 ? "Starting from the beginning" : request.DisplaySubtitle;
            LoadingKeys.Text = resumes ? "X  Start from the beginning        B  Back" : "B  Back";
            SetBackdrop(request.Background);
            if (request.HasRequestHeaders)
            {
                Log("warn", "This stream asks for custom request headers; the native player does not send them yet.");
            }

            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.Movie,
            };
            _mediaPlayer.MediaOpened += OnMediaOpened;
            _mediaPlayer.MediaFailed += OnMediaFailed;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.PlaybackSession.BufferingStarted += OnBufferingStarted;
            _mediaPlayer.PlaybackSession.BufferingEnded += OnBufferingEnded;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            _mediaPlayer.PlaybackSession.NaturalVideoSizeChanged += OnNaturalVideoSizeChanged;
            SetUpRemoteCommands(_mediaPlayer);

            Player.SetMediaPlayer(_mediaPlayer);
            ShowPlayerLayer();
            UpdateLoading();
            StartProgressTimer();

            _ = OpenSourceAsync(request, _mediaPlayer);
        }

        private void OnNaturalVideoSizeChanged(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_mediaPlayer?.PlaybackSession == sender)
                {
                    UpdateLetterbox();
                }
            });
        }

        // Paints the edges a Uniform-stretched picture leaves empty. The picture's shape comes from the decoded
        // size and its pixel aspect ratio (anamorphic files show wider than they are stored). The bars are
        // rounded down, so they never cover a line of the picture.
        private void UpdateLetterbox()
        {
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                double videoWidth = session?.NaturalVideoWidth ?? 0;
                double videoHeight = session?.NaturalVideoHeight ?? 0;
                if (_mediaPlayer?.Source is MediaPlaybackItem item && item.VideoTracks.Count > 0)
                {
                    var ratio = item.VideoTracks[0].GetEncodingProperties().PixelAspectRatio;
                    if (ratio != null && ratio.Numerator > 0 && ratio.Denominator > 0)
                    {
                        videoWidth = videoWidth * ratio.Numerator / ratio.Denominator;
                    }
                }

                var width = PlayerLayer.ActualWidth;
                var height = PlayerLayer.ActualHeight;
                if (videoWidth <= 0 || videoHeight <= 0 || width <= 0 || height <= 0)
                {
                    Letterbox.Visibility = Visibility.Collapsed;
                    return;
                }

                var scale = Math.Min(width / videoWidth, height / videoHeight);
                var top = Math.Max(0, Math.Floor((height - (videoHeight * scale)) / 2));
                var side = Math.Max(0, Math.Floor((width - (videoWidth * scale)) / 2));
                PlaceBar(BarTop, 0, 0, width, top);
                PlaceBar(BarBottom, 0, height - top, width, top);
                PlaceBar(BarLeft, 0, 0, side, height);
                PlaceBar(BarRight, width - side, 0, side, height);
                Letterbox.Visibility = top > 0 || side > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Letterbox.Visibility = Visibility.Collapsed;
                Log("warn", "Letterbox bars: " + ex.Message);
            }
        }

        private static void PlaceBar(FrameworkElement bar, double left, double top, double width, double height)
        {
            Canvas.SetLeft(bar, left);
            Canvas.SetTop(bar, top);
            bar.Width = Math.Max(0, width);
            bar.Height = Math.Max(0, height);
        }

        private void ShowPlayerLayer()
        {
            PlayerLayer.Visibility = Visibility.Visible;
            _typingReady = false; // The page loses focus to the player; set typing up again afterwards.
            CursorLayer.Visibility = Visibility.Collapsed;

            // Hide Stremio Web during native playback so the page stops rendering behind the video.
            if (_webView != null)
            {
                _webView.Visibility = Visibility.Collapsed;
            }

            Player.Focus(FocusState.Programmatic);
        }

        // The player screen comes up as soon as a stream is picked; the page sends 'play' once Stremio's
        // title, resume position and subtitles are known (a few seconds at most).
        private void ShowPreparing(double session, string title, string text)
        {
            if (!_nativePlayerEnabled)
            {
                return;
            }

            if (_mediaPlayer != null)
            {
                StopPlayback(resetDisplay: false);
            }

            _preparing = true;
            _startOverRequested = false;
            LoadingKeys.Text = "B  Back";
            _preparingSession = session;
            _preparingSince = DateTime.UtcNow;
            _current = null;
            LoadingTitle.Text = title;
            LoadingText.Text = text ?? string.Empty;
            SetBackdrop(null);
            Osd.Visibility = Visibility.Collapsed;
            UpNext.Visibility = Visibility.Collapsed;
            SkipIntro.Visibility = Visibility.Collapsed;
            CloseMenu();
            ShowPlayerLayer();
            UpdateLoading();
            StartProgressTimer();
        }

        private void SetBackdrop(string url)
        {
            try
            {
                Player.PosterSource = !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")
                    ? new Windows.UI.Xaml.Media.Imaging.BitmapImage(uri)
                    : null;
            }
            catch (Exception ex)
            {
                Player.PosterSource = null;
                Log("warn", "Backdrop failed: " + ex.Message);
            }
        }

        // Spinner while the page prepares the stream, while resuming / prebuffering, and on rebuffering.
        private void UpdateLoading()
        {
            var state = MediaPlaybackState.None;
            try
            {
                state = _mediaPlayer?.PlaybackSession?.PlaybackState ?? MediaPlaybackState.None;
            }
            catch (Exception)
            {
            }

            var show = _preparing || _starting || state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening;
            if (show && !_preparing && !_starting)
            {
                LoadingText.Text = "Buffering";
            }

            LoadingPanel.Visibility = show && PlayerLayer.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_mediaPlayer?.PlaybackSession != sender)
                {
                    return;
                }

                UpdateLoading();
                ReportProgress(force: true);
                if (Osd.Visibility == Visibility.Visible)
                {
                    UpdateOsd();
                }
            });
        }

        // ---- Session events for the page (Stremio's library, Continue Watching, next episode) ----------
        private void PostPlayerEvent(string name, bool withTimes)
        {
            var session = _current?.Session ?? _preparingSession;
            var message = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue("player"),
                ["event"] = JsonValue.CreateStringValue(name),
                ["session"] = JsonValue.CreateNumberValue(session),
            };

            var playback = _mediaPlayer?.PlaybackSession;
            if (withTimes && playback != null && _hasPlayed)
            {
                try
                {
                    var position = _reopening ? _reopenPosition : (_scrubTarget ?? playback.Position);
                    message["time"] = JsonValue.CreateNumberValue(Math.Round(position.TotalMilliseconds));
                    message["duration"] = JsonValue.CreateNumberValue(Math.Round(playback.NaturalDuration.TotalMilliseconds));
                    message["paused"] = JsonValue.CreateBooleanValue(playback.PlaybackState != MediaPlaybackState.Playing);
                }
                catch (Exception ex)
                {
                    Log("warn", "Could not read the position: " + ex.GetType().Name);
                }
            }

            try
            {
                _webView?.CoreWebView2?.PostWebMessageAsString(message.Stringify());
            }
            catch (Exception ex)
            {
                Log("warn", "Could not send " + name + " to the page: " + ex.Message);
            }
        }

        private void StartProgressTimer()
        {
            if (_progressTimer == null)
            {
                _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _progressTimer.Tick += (sender, e) => OnProgressTick();
            }

            _progressTimer.Start();
        }

        private void OnProgressTick()
        {
            if (_preparing && _mediaPlayer == null)
            {
                // The page normally answers within a few seconds; never leave the viewer on a spinner.
                if (DateTime.UtcNow - _preparingSince > TimeSpan.FromSeconds(45))
                {
                    Log("warn", "No stream from the page after 45 s");
                    PostPlayerEvent("cancel", false);
                    _cancelledSession = _preparingSession;
                    _preparing = false;
                    StopPlayback(resetDisplay: true);
                    ShowToast("The stream didn't load. Try again or pick another stream.");
                }

                return;
            }

            UpdateLoading();
            ReportProgress(force: false);
            UpdateUpNext();
            UpdateSkipIntro();
        }

        // Every 10 s while playing (Stremio's own player reports more often, but only the latest value is
        // kept), and at once on pause/play changes.
        private void ReportProgress(bool force)
        {
            var playback = _mediaPlayer?.PlaybackSession;
            if (playback == null || !_hasPlayed || _starting)
            {
                return;
            }

            bool paused;
            try
            {
                paused = playback.PlaybackState != MediaPlaybackState.Playing;
            }
            catch (Exception)
            {
                return;
            }

            if (paused && playback.PlaybackState == MediaPlaybackState.Buffering)
            {
                return; // Buffering is not a pause.
            }

            var due = unchecked(Environment.TickCount - _lastProgressTick) >= 10000;
            if ((force && paused != _reportedPaused) || (due && (!paused || _reportedPaused != true)))
            {
                _reportedPaused = paused;
                _lastProgressTick = Environment.TickCount;
                PostPlayerEvent("progress", true);
            }
        }

        // ---- Next episode -------------------------------------------------------------------------------
        private void SetNextLabel(string label)
        {
            _nextLabel = string.IsNullOrEmpty(label) ? null : label;
            Log("info", "Next episode: " + (_nextLabel ?? "none"));
            if (_mediaPlayer != null)
            {
                _mediaPlayer.CommandManager.NextBehavior.EnablingRule = _nextLabel != null ? MediaCommandEnablingRule.Always : MediaCommandEnablingRule.Never;
            }

            UpdateUpNext();
        }

        // Stremio's "next video" card: the last 40 s of an episode, while a next episode exists.
        private void UpdateUpNext()
        {
            var visible = false;
            var playback = _mediaPlayer?.PlaybackSession;
            if (playback != null && _nextLabel != null && !_upNextDismissed && _hasPlayed && !_starting && _scrubTarget == null && TrackMenu.Visibility != Visibility.Visible)
            {
                try
                {
                    var duration = playback.NaturalDuration;
                    visible = duration > TimeSpan.FromMinutes(2) && duration - playback.Position <= TimeSpan.FromSeconds(40);
                }
                catch (Exception)
                {
                }
            }

            if (visible)
            {
                UpNextTitle.Text = _nextLabel;
                UpNextHint.Text = _binge ? "Plays automatically   A  Play now   B  Hide" : "A  Play now   B  Hide";
            }

            UpNext.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Skip intro --------------------------------------------------------------------------------
        private void ApplyIntro(double fromMs, double toMs)
        {
            if (toMs > fromMs && toMs > 0)
            {
                _introFrom = TimeSpan.FromMilliseconds(Math.Max(0, fromMs));
                _introTo = TimeSpan.FromMilliseconds(toMs);
                Log("info", "Intro: " + FormatTime(_introFrom.Value) + " to " + FormatTime(_introTo.Value));
            }
            else
            {
                _introFrom = null;
                _introTo = null;
            }
        }

        // Stremio's "Skip intro" button: shown while playback is inside the intro Stremio knows about.
        private void UpdateSkipIntro()
        {
            var visible = false;
            var playback = _mediaPlayer?.PlaybackSession;
            if (playback != null && _introFrom.HasValue && _introTo.HasValue && !_skipIntroDismissed && _hasPlayed && !_starting &&
                _scrubTarget == null && TrackMenu.Visibility != Visibility.Visible)
            {
                try
                {
                    var position = playback.Position;
                    visible = position >= _introFrom.Value && position < _introTo.Value - TimeSpan.FromSeconds(2);
                }
                catch (Exception)
                {
                }
            }

            SkipIntro.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SkipIntroNow()
        {
            if (_mediaPlayer?.PlaybackSession == null || !_introTo.HasValue)
            {
                return;
            }

            _skipIntroDismissed = true;
            SkipIntro.Visibility = Visibility.Collapsed;
            Log("info", "Skip intro");
            _scrubTarget = _introTo.Value;
            CommitScrub();
        }

        private void PlayNext()
        {
            if (_mediaPlayer == null || _nextLabel == null)
            {
                return;
            }

            Log("info", "Next episode requested: " + _nextLabel);
            PostPlayerEvent("next", true);
            var label = _nextLabel;
            var session = _current?.Session ?? 0;
            StopPlayback(resetDisplay: false);
            ShowPreparing(session, label, "Finding the next stream");
        }

        // ---- Xbox media remote / system media controls --------------------------------------------------
        private void SetUpRemoteCommands(MediaPlayer player)
        {
            try
            {
                var commands = player.CommandManager;
                commands.IsEnabled = true;
                commands.NextBehavior.EnablingRule = _nextLabel != null ? MediaCommandEnablingRule.Always : MediaCommandEnablingRule.Never;
                commands.FastForwardBehavior.EnablingRule = MediaCommandEnablingRule.Always;
                commands.RewindBehavior.EnablingRule = MediaCommandEnablingRule.Always;
                commands.NextReceived += (sender, args) =>
                {
                    args.Handled = true;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (player == _mediaPlayer)
                        {
                            PlayNext();
                        }
                    });
                };
                commands.FastForwardReceived += (sender, args) =>
                {
                    args.Handled = true;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => RemoteSeek(player, 30));
                };
                commands.RewindReceived += (sender, args) =>
                {
                    args.Handled = true;
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => RemoteSeek(player, -10));
                };
            }
            catch (Exception ex)
            {
                Log("warn", "Media remote commands unavailable: " + ex.Message);
            }
        }

        private void RemoteSeek(MediaPlayer player, double seconds)
        {
            if (player == _mediaPlayer && !_starting)
            {
                Scrub(player.PlaybackSession, seconds, accelerate: false);
            }
        }

        private static void ApplyDisplayProperties(MediaPlaybackItem item, PlayRequest request)
        {
            var props = item.GetDisplayProperties();
            props.Type = Windows.Media.MediaPlaybackType.Video;
            props.VideoProperties.Title = request.DisplayTitle;
            props.VideoProperties.Subtitle = request.DisplaySubtitle;
            item.ApplyDisplayProperties(props);
        }

        // ---- App leaves the screen (Home button, another app) ------------------------------------------
        private void OnEnteredBackground(object sender, Windows.ApplicationModel.EnteredBackgroundEventArgs e)
        {
            var deferral = e.GetDeferral();
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
            {
                try
                {
                    if (_mediaPlayer != null && !_starting && _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                    {
                        CommitScrub();
                        _mediaPlayer.Pause();
                        Log("info", "Paused: app went to the background");
                    }

                    if (_hasPlayed)
                    {
                        _reportedPaused = true;
                        PostPlayerEvent("progress", true); // Save the position in case the app is not resumed.
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }

        private void OnLeavingBackground(object sender, Windows.ApplicationModel.LeavingBackgroundEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_mediaPlayer != null)
                {
                    ShowOsd(); // Shows "Paused" and how to continue.
                }
            });
        }

        // Resolves the addon URL (redirects, size), then either feeds the player from the app's own
        // multi-connection RAM buffer or, as a fallback, hands it the URL directly.
        private async Task OpenSourceAsync(PlayRequest request, MediaPlayer player)
        {
            try
            {
                // A server that refuses connections for a moment (seen with a TorBox CDN node) gets two more
                // tries; when it stays unreachable, say so. Handing the player the URL then only failed later
                // with "the Xbox can't play this format".
                var probe = await HttpChunkCache.ResolveAsync(request.Url, LogAsync);
                for (var attempt = 2; probe == null && attempt <= 3 && player == _mediaPlayer; attempt++)
                {
                    LoadingText.Text = "Connecting to the stream's server (try " + attempt + " of 3)";
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 2 ? 1.5 : 3));
                    if (player != _mediaPlayer)
                    {
                        return;
                    }

                    probe = await HttpChunkCache.ResolveAsync(request.Url, LogAsync);
                }

                if (player != _mediaPlayer)
                {
                    return; // Stopped while resolving.
                }

                if (probe == null && _connections > 0)
                {
                    Log("error", "The stream's server could not be reached (3 tries)");
                    FailPlayback("Couldn't reach this stream's server. Try again in a moment, or pick another stream.");
                    return;
                }

                if (request.StartPositionMs > 0 && !_startOverRequested)
                {
                    LoadingText.Text = "Resuming from " + FormatTime(TimeSpan.FromMilliseconds(request.StartPositionMs));
                }

                _fileSize = probe?.Size ?? 0;
                var contentType = GuessContentType(request, probe?.ContentType);
                MediaSource source;
                if (_connections > 0 && probe != null && probe.AcceptsRanges && probe.Size > 0 && contentType != null)
                {
                    var ahead = PickReadAheadBytes();
                    _cache = new HttpChunkCache(request.Url, probe.FinalUrl, probe.Size, _connections, ahead, LogAsync);
                    if (request.StartPositionMs > 0)
                    {
                        _cache.HoldReadAhead(true); // Resuming further on: no read-ahead from the start (see ResumeAsync).
                    }

                    _contentType = contentType;
                    _lastCacheBytes = 0;
                    source = MediaSource.CreateFromStream(new BufferedHttpStream(_cache, 0), contentType);
                    Log("info", string.Format("Buffered playback: {0} connections, {1} MB read-ahead, {2}", _connections, ahead >> 20, contentType));
                }
                else
                {
                    Log("info", "Direct URL playback (" + (_connections == 0 ? "selected in debug log" :
                        probe == null ? "probe failed" : !probe.AcceptsRanges ? "no range support" : contentType == null ? "unknown container" : "size unknown") + ")");
                    source = MediaSource.CreateFromUri(new Uri(request.Url));
                }

                player.Source = CreatePlaybackItem(request, source, -1);
            }
            catch (Exception ex)
            {
                if (player != _mediaPlayer)
                {
                    return;
                }

                Log("error", "Could not open stream: " + ex.Message);
                FailPlayback("Couldn't open this stream (" + ex.Message + "). Try another stream.");
            }
        }

        private MediaPlaybackItem CreatePlaybackItem(PlayRequest request, MediaSource source, int audioIndex)
        {
            if (_attachSubtitles)
            {
                AddExternalSubtitles(source, request);
            }
            else
            {
                Log("info", "External subtitles: off (" + request.Subtitles.Count + " available)");
            }

            var item = new MediaPlaybackItem(source);

            // Tracks appear while the file opens, before playback: the place to pick the audio track
            // without switching formats under a running player.
            item.AudioTracksChanged += (sender2, args2) => SelectInitialAudio(sender2, request, audioIndex);
            item.TimedMetadataTracksChanged += (sender2, args2) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                LogSubtitleTracks(sender2);
                AutoSelectSubtitles(sender2);
            });
            try
            {
                ApplyDisplayProperties(item, request);
            }
            catch (Exception ex)
            {
                Log("warn", "Media info for the system controls failed: " + ex.Message);
            }

            return item;
        }

        // Runs on a media thread while the file opens. audioIndex >= 0: the track asked for in an audio switch.
        private void SelectInitialAudio(MediaPlaybackItem item, PlayRequest request, int audioIndex)
        {
            if (!_starting)
            {
                return; // Never switch under a running player.
            }

            try
            {
                var tracks = item.AudioTracks;
                if (tracks.Count == 0)
                {
                    return;
                }

                var decision = audioIndex >= 0 ? null : PickAudioTrack(tracks, request);
                var pick = audioIndex >= 0 ? audioIndex : decision.Index;
                if (pick < 0 || pick >= tracks.Count)
                {
                    return;
                }

                var current = tracks.SelectedIndex;
                if (pick != current)
                {
                    tracks.SelectedIndex = pick;
                }

                var chosen = tracks[pick];
                if (decision != null)
                {
                    var before = current >= 0 && current < tracks.Count ? tracks[current] : null;
                    var notes = new List<string>();
                    if (decision.OriginalLanguage)
                    {
                        notes.Add("Original " + LanguageName(decision.Language) + " audio");
                    }

                    if (before != null && pick != current && MaybeSilent(before) && !MaybeSilent(chosen))
                    {
                        notes.Add(AudioFormatName(chosen) + " instead of " + (IsDtsHd(before) ? "DTS-HD (no sound on Xbox)" : "Dolby TrueHD (often silent on Xbox)"));
                    }
                    else if (IsDtsHd(chosen))
                    {
                        notes.Add("DTS-HD audio has no sound on Xbox. Press Y for another track, or pick another stream");
                    }
                    else if (IsTrueHd(chosen))
                    {
                        notes.Add("Dolby TrueHD can be silent on Xbox. No sound? Press Y for another track, or pick another stream");
                    }

                    _audioNote = notes.Count == 0 ? null : string.Join("   ·   ", notes) + (MaybeSilent(chosen) ? string.Empty : "   ·   Y to change");
                    _originalAudio = decision.OriginalLanguage;
                    _audioLanguage = string.IsNullOrEmpty(decision.Language) || decision.Language == "und"
                        ? (AudioChoice.IsEnglishSpeaking(request.Country) ? "en" : null) // Untagged audio of an English-language title.
                        : decision.Language;
                }

                var country = string.IsNullOrEmpty(request.Country) ? "unknown" : request.Country;
                LogAsync("info", string.Format("Audio track {0}/{1}: {2}{3}{4}", pick + 1, tracks.Count, DescribeAudio(chosen),
                    pick != current ? " (default was " + (current + 1) + (audioIndex >= 0 ? ", audio switch)" : ")") : string.Empty,
                    decision == null ? string.Empty
                        : decision.OriginalLanguage ? ", original language of a title from " + country + ": English subtitles on"
                        : ", country " + country));

                // Subtitles wait for this choice (English ones go on for a foreign title in its own language).
                _audioDecided = true;
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => AutoSelectSubtitles(item));
            }
            catch (Exception ex)
            {
                LogAsync("warn", "Choosing the audio track failed: " + ex.Message);
            }
        }

        // See AudioChoice: English (or a foreign title's own language), then the best track the Xbox plays reliably.
        private static AudioDecision PickAudioTrack(IReadOnlyList<AudioTrack> tracks, PlayRequest request)
        {
            var options = tracks.Select(track =>
            {
                var props = track.GetEncodingProperties();
                return new AudioOption(NormalizeLanguage(track.Language), track.Label, FormatOf(track), (int)props.ChannelCount);
            }).ToList();
            return AudioChoice.Choose(options, request.Country, NormalizeLanguage(request.AudioLanguage));
        }

        private static AudioFormat FormatOf(AudioTrack track)
        {
            switch (AudioFormatName(track))
            {
                case "Dolby TrueHD": return AudioFormat.TrueHd;
                case "DTS-HD": return AudioFormat.DtsHd;
                case "Dolby Digital Plus": return AudioFormat.DolbyDigitalPlus;
                case "Dolby Digital": return AudioFormat.DolbyDigital;
                case "DTS": return AudioFormat.Dts;
                default:
                    var subtype = (track.GetEncodingProperties().Subtype ?? string.Empty).ToUpperInvariant();
                    return subtype.Contains("AAC") ? AudioFormat.Aac : AudioFormat.Other;
            }
        }

        // DTS-HD (Master Audio, from Blu-ray remuxes) is listed by the Xbox player but plays no sound. Its
        // MFAudioFormat_DTS_HD subtype has no WinRT name either, so it reads as its GUID.
        private static bool IsDtsHd(AudioTrack track)
        {
            var subtype = (track.GetEncodingProperties().Subtype ?? string.Empty).ToUpperInvariant();
            var label = (track.Label ?? string.Empty).ToUpperInvariant();
            return subtype.Contains("A2E58EB7-0FA9-48BB-A40C-FA0E156D0645") || subtype.Contains("DTS_HD") || subtype.Contains("DTSHD") ||
                label.Contains("DTS-HD") || label.Contains("DTS HD") || label.Contains("DTS:X") || label.Contains("DTS-X");
        }

        // Formats the Xbox player lists but may not play: the app avoids them and says so when it can't.
        private static bool MaybeSilent(AudioTrack track) => IsTrueHd(track) || IsDtsHd(track);

        private static bool IsTrueHd(AudioTrack track)
        {
            var subtype = (track.GetEncodingProperties().Subtype ?? string.Empty).ToUpperInvariant();
            var label = (track.Label ?? string.Empty).ToUpperInvariant();

            // MEDIASUBTYPE_DOLBY_TRUEHD has no WinRT name; the subtype then reads as its GUID.
            return subtype.Contains("TRUEHD") || subtype.Contains("MLP") || subtype.Contains("EB27CEC4-163E-4CA3-8B74-8E25F91B517E") ||
                label.Contains("TRUEHD") || label.Contains("TRUE HD");
        }

        // Live when the format stays the same; otherwise reopen the file with the new track (see _reopening).
        private void SwitchAudioTrack(MediaPlaybackItem item, int index, string label)
        {
            var tracks = item.AudioTracks;
            var current = tracks.SelectedIndex;
            if (index == current || index < 0 || index >= tracks.Count)
            {
                return;
            }

            if (current >= 0 && AudioFormatName(tracks[current]) == AudioFormatName(tracks[index]))
            {
                tracks.SelectedIndex = index;
                _osdMessage = "Audio: " + label;
                Log("info", "Audio track " + (index + 1) + "/" + tracks.Count + " (same format, live): " + label);
                return;
            }

            ReopenWithAudio(index, current, label);
        }

        private void ReopenWithAudio(int index, int previous, string label)
        {
            var player = _mediaPlayer;
            if (player == null || _current == null)
            {
                return;
            }

            var session = player.PlaybackSession;
            var oldItem = player.Source as MediaPlaybackItem;
            _scrubTimer?.Stop();
            if (!_reopening)
            {
                _reopenPosition = _scrubTarget ?? session.Position;
                _reopenWasPaused = session.PlaybackState != MediaPlaybackState.Playing;
                _restoreSubtitle = oldItem != null ? CurrentSubtitleLabel(oldItem) : null;
            }

            _scrubTarget = null;
            _reopenFallback = previous;
            _reopening = true;
            _starting = true;
            _osdMessage = "Audio: " + label;
            LoadingTitle.Text = _current.DisplayTitle;
            LoadingText.Text = "Switching audio to " + label;
            Osd.Visibility = Visibility.Collapsed;
            UpNext.Visibility = Visibility.Collapsed;
            SkipIntro.Visibility = Visibility.Collapsed;
            UpdateLoading();
            Log("info", "Audio switch to another format: reopening at " + FormatTime(_reopenPosition) + " with track " + (index + 1) + " (" + label + ")");

            try
            {
                player.Pause();
                var source = _cache != null && _contentType != null
                    ? MediaSource.CreateFromStream(new BufferedHttpStream(_cache, 0, "audio switch"), _contentType)
                    : MediaSource.CreateFromUri(new Uri(_current.Url));
                player.Source = CreatePlaybackItem(_current, source, index);
            }
            catch (Exception ex)
            {
                Log("error", "Audio switch failed: " + ex.Message);
                FailPlayback("Couldn't switch the audio track (" + ex.Message + ").");
            }
        }

        private static string CurrentSubtitleLabel(MediaPlaybackItem item)
        {
            var tracks = item.TimedMetadataTracks;
            for (var i = 0; i < tracks.Count; i++)
            {
                if (IsSubtitleTrack(tracks[i]) && tracks.GetPresentationMode((uint)i) != TimedMetadataTrackPresentationMode.Disabled)
                {
                    return DescribeSubtitle(tracks[i]);
                }
            }

            return string.Empty;
        }

        private void AddOsdNote(string note)
        {
            if (!string.IsNullOrEmpty(note))
            {
                _osdMessage = string.IsNullOrEmpty(_osdMessage) ? note : _osdMessage + "   ·   " + note;
            }
        }

        // Stays in the app: a message instead of the debug log, and Stremio's stream list to pick another.
        private void FailPlayback(string message)
        {
            PostPlayerEvent("failed", false);
            StopPlayback(resetDisplay: true);
            ShowToast(message, 8);
        }

        // Leaves room for the app (about 1.3 GB during 4K playback) inside the console's memory budget.
        private static long PickReadAheadBytes()
        {
            const long mb = 1024 * 1024;
            var free = (long)MemoryManager.AppMemoryUsageLimit - (long)MemoryManager.AppMemoryUsage;
            return Math.Max(128 * mb, Math.Min(768 * mb, free - (1024 * mb)));
        }

        private static string GuessContentType(PlayRequest request, string probedType)
        {
            var text = string.Join(" ", request.FileName, request.Description, request.Name, request.Url);
            var match = Regex.Match(text, @"\.(mkv|mp4|m4v|webm|mov)(?![a-z0-9])", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "mkv":
                        return "video/x-matroska";
                    case "webm":
                        return "video/webm";
                    case "mov":
                        return "video/quicktime";
                    default:
                        return "video/mp4";
                }
            }

            return !string.IsNullOrEmpty(probedType) && probedType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? probedType : null;
        }

        // Some encodes (e.g. x265 Blu-ray MKVs) carry HDR only in the HEVC bitstream, and the Xbox reports
        // no transfer function for them. Used only in that case.
        private static bool NameSaysHdr(PlayRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var text = string.Join(" ", request.FileName, request.Description, request.Name);
            return Regex.IsMatch(text, @"(?<![a-z])(hdr|hdr10|hdr10\+|dv|dovi|dolby.?vision)(?![a-z])", RegexOptions.IgnoreCase);
        }

        private static bool NameSaysDolbyVision(PlayRequest request)
        {
            if (request == null)
            {
                return false;
            }

            var text = string.Join(" ", request.FileName, request.Description, request.Name);
            return Regex.IsMatch(text, @"(?<![a-z])(dv|dovi|dolby.?vision)(?![a-z])", RegexOptions.IgnoreCase);
        }

        // Logs (0.1.3.5) show the player reading a second position far ahead of playback, and playback
        // slowing whenever that read waits; a manual +60 s then back during playback removed it every time,
        // while the same seek done before playing (0.1.3.7) did not hold on every file.
        private async Task RestartSeekBeforePlayAsync(MediaPlayer player, int seconds, TimeSpan origin)
        {
            var session = player.PlaybackSession;
            if (_cache == null || !session.CanSeek || session.NaturalDuration < origin + TimeSpan.FromSeconds(seconds + 60))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            var forward = await SeekAndWaitAsync(session, origin + TimeSpan.FromSeconds(seconds));
            var back = await SeekAndWaitAsync(session, origin);
            Log(forward && back ? "info" : "warn", string.Format("Restart seek before play (+{0} s, then {1:hh\\:mm\\:ss}) {2} in {3} ms",
                seconds, origin, forward && back ? "done" : "timed out", watch.ElapsedMilliseconds));
        }

        // X on the loading screen: play from 0:00 instead of where Stremio's library left off. Before the resume
        // seek, ResumeAsync skips it; after it (still loading), OnMediaOpened seeks back to the start.
        private void RequestStartOver()
        {
            if (_startOverRequested || (_current != null && _current.StartPositionMs <= 0))
            {
                return; // Already asked, or nothing to resume.
            }

            _startOverRequested = true;
            _seekAbort?.TrySetResult(false); // Stop waiting for the resume seek; OnMediaOpened goes to 0:00 next.
            LoadingText.Text = "Starting from the beginning";
            LoadingKeys.Text = "B  Back";
            Log("info", "Start from the beginning (X on the loading screen)");
        }

        // Where Stremio's library says this video was left; ignored when it is (almost) at the end.
        private async Task<TimeSpan> ResumeAsync(MediaPlayer player)
        {
            var session = player.PlaybackSession;
            if (_reopening)
            {
                if (_reopenPosition > TimeSpan.Zero && session.CanSeek)
                {
                    var back = await SeekAndWaitAsync(session, _reopenPosition, true);
                    Log(back ? "info" : "warn", "Audio switch: back at " + FormatTime(_reopenPosition) + (back ? string.Empty : " (seek timed out)"));
                }

                return _reopenPosition;
            }

            var target = TimeSpan.FromMilliseconds(_current?.StartPositionMs ?? 0);
            if (_startOverRequested && target > TimeSpan.Zero)
            {
                Log("info", "Starting from the beginning instead of resuming at " + FormatTime(target));
                AddOsdNote("Started from the beginning");
                return TimeSpan.Zero;
            }

            var duration = session.NaturalDuration;
            if (target < TimeSpan.FromSeconds(5) || !session.CanSeek || (duration > TimeSpan.Zero && target > duration - TimeSpan.FromSeconds(60)))
            {
                return TimeSpan.Zero;
            }

            var watch = Stopwatch.StartNew();
            var done = await SeekAndWaitAsync(session, target, true);
            Log(done ? "info" : "warn", string.Format("Resume at {0:hh\\:mm\\:ss} {1} in {2} ms", target, done ? "done" : "timed out", watch.ElapsedMilliseconds));
            AddOsdNote("Resumed from " + FormatTime(target) + "   ·   LB to go back 10 s");
            return target;
        }

        // Same as a manual skip: once playing, jump forward, stay a second, and jump back.
        private async Task RestartSeekAfterPlayAsync(MediaPlayer player, int seconds)
        {
            var session = player.PlaybackSession;
            if (_cache == null || !session.CanSeek || session.NaturalDuration < TimeSpan.FromSeconds(seconds + 60))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            while (player == _mediaPlayer && watch.Elapsed < TimeSpan.FromSeconds(15) &&
                !(session.PlaybackState == MediaPlaybackState.Playing && session.Position >= TimeSpan.FromSeconds(1)))
            {
                await Task.Delay(100);
            }

            if (player != _mediaPlayer)
            {
                return;
            }

            var from = session.Position;
            Log("info", string.Format("Restart seek after play: +{0} s from {1:hh\\:mm\\:ss} (waited {2} ms for playback)", seconds, from, watch.ElapsedMilliseconds));
            watch.Restart();
            var forward = await SeekAndWaitAsync(session, from + TimeSpan.FromSeconds(seconds));
            await Task.Delay(1000);
            var back = player == _mediaPlayer && await SeekAndWaitAsync(session, from);
            Log(forward && back ? "info" : "warn", string.Format("Restart seek after play {0} in {1} ms",
                forward && back ? "done" : "timed out", watch.ElapsedMilliseconds));
        }

        private string DescribeRestartSeek()
        {
            var seconds = RestartSeekModes[_restartSeekMode];
            return seconds == 0 ? "off" : seconds > 0 ? "+" + seconds + " s after play" : "+" + (-seconds) + " s before play";
        }

        private async Task<bool> SeekAndWaitAsync(MediaPlaybackSession session, TimeSpan target, bool stopOldDownloads = false)
        {
            var done = new TaskCompletionSource<bool>();
            var abort = new TaskCompletionSource<bool>();
            _seekAbort = abort;
            Windows.Foundation.TypedEventHandler<MediaPlaybackSession, object> handler = (s, e) => done.TrySetResult(true);
            session.SeekCompleted += handler;
            try
            {
                _cache?.SeekStarted(stopOldDownloads);
                session.Position = target;
                return await Task.WhenAny(done.Task, Task.Delay(10000), abort.Task) == done.Task;
            }
            finally
            {
                session.SeekCompleted -= handler;
                if (_seekAbort == abort)
                {
                    _seekAbort = null;
                }
            }
        }

        // A paused player finishes the resume seek without reading the file there: it reads the new spot only
        // once playing. Until then the read-ahead window sat at the start of the file, so the prebuffer below
        // counted (and downloaded) the wrong part and playback began with nothing buffered. Playing muted
        // behind the loading screen until the first read lands moves the window to the resume point.
        private async Task WaitForResumeReadAsync(MediaPlayer player)
        {
            var cache = _cache;
            if (cache == null)
            {
                return;
            }

            var before = cache.PlayheadChunk;
            var wasMuted = player.IsMuted;
            var watch = Stopwatch.StartNew();
            player.IsMuted = true;
            player.Play();
            while (player == _mediaPlayer && watch.Elapsed < TimeSpan.FromSeconds(4) && cache.PlayheadChunk == before)
            {
                await Task.Delay(50);
            }

            if (player != _mediaPlayer)
            {
                return;
            }

            player.Pause();
            player.IsMuted = wasMuted;
            var moved = cache.PlayheadChunk != before;
            Log(moved ? "info" : "warn", string.Format("Resume read {0} chunk {1} after {2} ms",
                moved ? "reached" : "did not reach a new spot, still at", cache.PlayheadChunk, watch.ElapsedMilliseconds));
        }

        // Holds playback until enough video is in RAM for the first busy scenes (or 15 s pass). How much depends
        // on the download against the file's bitrate, measured while waiting: well ahead (1.25x and more) the
        // buffer only grows once playing, so 4 s is enough and the video starts in a few seconds; close to it,
        // 8 s; slower, the full 15 s, which a remux on a slow link needs.
        private async Task PrebufferAsync(MediaPlayer player)
        {
            var cache = _cache;
            var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (cache == null || duration <= 0)
            {
                return;
            }

            var bytesPerSecond = cache.Size / duration;
            var most = (long)Math.Min(cache.AheadBytes / 2, bytesPerSecond * 15);
            var watch = Stopwatch.StartNew();
            var startBytes = cache.DownloadedBytes;
            var ratio = 0.0;
            while (player == _mediaPlayer && watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                var buffered = cache.BufferedAheadBytes;
                if (buffered >= most)
                {
                    break;
                }

                if (watch.Elapsed > TimeSpan.FromSeconds(1.5))
                {
                    ratio = (cache.DownloadedBytes - startBytes) / watch.Elapsed.TotalSeconds / bytesPerSecond;
                    var seconds = ratio >= 1.25 ? 4 : ratio >= 1.1 ? 8 : 15;
                    if (buffered >= bytesPerSecond * seconds)
                    {
                        break;
                    }
                }

                await Task.Delay(250);
            }

            var have = cache.BufferedAheadBytes;
            Log("info", string.Format("Prebuffered {0} MB ({1:0.0} s of video) at chunk {2} in {3} ms{4}",
                have >> 20, have / bytesPerSecond, cache.PlayheadChunk, watch.ElapsedMilliseconds,
                ratio > 0 ? string.Format(", download {0:0.00}x the bitrate", ratio) : string.Empty));
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var video = DescribeMedia(sender, out var width, out var height, out var frameRate, out var transferFunction, out var subtype);
                Log("info", video);
                UpdateLetterbox();
                Log("info", "Letterbox bars: " + (Letterbox.Visibility == Visibility.Visible
                    ? string.Format("{0:0}px top/bottom, {1:0}px sides", BarTop.Height, BarLeft.Width) : "none"));
                var reopened = _reopening; // Audio switch: same video, the display is already in the right mode.

                if (!reopened)
                {
                    // Decided from what the decoder reports, not from release names: Dolby Vision only when
                    // the Xbox decodes it as DV (subtype DVHE/DVH1); DV MKVs decode as HEVC + HDR10 base layer.
                    var range = IsDolbyVisionSubtype(subtype) ? VideoRange.DolbyVision : RangeFromTransferFunction(transferFunction);
                    var fromName = false;
                    if (range == VideoRange.Sdr && transferFunction == null && NameSaysHdr(_current))
                    {
                        range = VideoRange.Hdr10;
                        fromName = true;
                    }

                    Log("info", "Video range: " + range + " (decoder subtype " + subtype + ", transfer function " + (transferFunction?.ToString() ?? "unknown") + (fromName ? ", HDR from release name" : string.Empty) + ")");
                    Log("info", await _displayModes.SwitchAsync(width, height, frameRate, range));
                    if (range == VideoRange.Hdr10 && NameSaysDolbyVision(_current))
                    {
                        // Profile 7/8: the Xbox decodes Dolby Vision only for profile 5; the HDR10 base layer plays.
                        Log("info", "Dolby Vision in the name but decoded as HDR10 (profile 7/8 base layer)");
                        AddOsdNote("Dolby Vision profile 7/8 plays as HDR10 on Xbox");
                    }
                }

                // The player can fail (and be closed) while the display mode switches; its session then throws.
                if (sender != _mediaPlayer)
                {
                    return;
                }

                if (!_displayRequestActive)
                {
                    _displayRequest.RequestActive();
                    _displayRequestActive = true;
                }

                var origin = await ResumeAsync(sender);
                if (origin > TimeSpan.Zero && !_startOverRequested)
                {
                    await WaitForResumeReadAsync(sender);
                }

                _cache?.HoldReadAhead(false);
                if (sender != _mediaPlayer)
                {
                    return;
                }

                if (_startOverRequested && origin > TimeSpan.Zero && !reopened)
                {
                    // X during the resume seek: straight to the start, before buffering around the old spot.
                    var start = await SeekAndWaitAsync(sender.PlaybackSession, TimeSpan.Zero, true);
                    if (sender != _mediaPlayer)
                    {
                        return;
                    }

                    Log(start ? "info" : "warn", "Started from the beginning instead of " + FormatTime(origin) + (start ? string.Empty : " (seek timed out)"));
                    _osdMessage = "Started from the beginning";
                    origin = TimeSpan.Zero;
                }

                var restartSeek = reopened ? 0 : RestartSeekModes[_restartSeekMode];
                if (restartSeek < 0)
                {
                    await RestartSeekBeforePlayAsync(sender, -restartSeek, origin);
                }

                await PrebufferAsync(sender);
                if (sender != _mediaPlayer)
                {
                    return;
                }

                if (!(reopened && _reopenWasPaused))
                {
                    sender.Play();
                }

                // The restart seek (see RestartSeekAfterPlayAsync) jumps ahead and back once playback runs. Done
                // in view it looked like the video skipping, so it runs muted, behind the loading screen.
                if (restartSeek > 0)
                {
                    var wasMuted = sender.IsMuted;
                    sender.IsMuted = true;
                    Curtain.Visibility = Visibility.Visible;
                    LoadingText.Text = "Almost ready";
                    try
                    {
                        await RestartSeekAfterPlayAsync(sender, restartSeek);
                    }
                    finally
                    {
                        if (sender == _mediaPlayer)
                        {
                            sender.IsMuted = wasMuted;
                            Curtain.Visibility = Visibility.Collapsed;
                        }
                    }

                    if (sender != _mediaPlayer)
                    {
                        return;
                    }
                }

                if (_startOverRequested && origin > TimeSpan.Zero && !reopened)
                {
                    // X came after the resume seek: go back to the start before the picture shows.
                    var back = await SeekAndWaitAsync(sender.PlaybackSession, TimeSpan.Zero, true);
                    if (sender != _mediaPlayer)
                    {
                        return;
                    }

                    Log(back ? "info" : "warn", "Started from the beginning after resuming at " + FormatTime(origin) + (back ? string.Empty : " (seek timed out)"));
                    _osdMessage = "Started from the beginning"; // Replaces "Resumed from ...".
                }

                LoadingKeys.Text = "B  Back";
                _starting = false;
                _reopening = false;
                _reopenFallback = -1;
                _hasPlayed = true;
                AddOsdNote(_audioNote);
                _audioNote = null;
                UpdateLoading();
                StartDiagnostics();
                ShowOsd();
            });
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                var code = args.ExtendedErrorCode == null ? string.Empty : string.Format(" (0x{0:X8})", args.ExtendedErrorCode.HResult);
                if (_reopening && _reopenFallback >= 0)
                {
                    var fallback = _reopenFallback;
                    _reopenFallback = -1;
                    Log("warn", "Audio switch: the new track failed to open (" + args.Error + code + "); going back to track " + (fallback + 1));
                    _osdMessage = "That audio track didn't play; back to the previous one";
                    ReopenWithAudio(fallback, -1, "the previous track");
                    return;
                }

                LogPlaybackStatus("at failure");
                Log("error", "Native player failed: " + args.Error + code + " " + args.ErrorMessage);
                var reason = args.Error == MediaPlayerError.NetworkError ? "the server stopped responding"
                    : args.Error == MediaPlayerError.SourceNotSupported ? "the Xbox can't play this format"
                    : args.Error == MediaPlayerError.DecodingError ? "the video couldn't be decoded"
                    : "playback failed" + code;
                if (_hasPlayed)
                {
                    PostPlayerEvent("progress", true); // Keep the position for a retry with another stream.
                }

                FailPlayback("Couldn't play this stream: " + reason + ". Pick another stream to keep watching.");
            });
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (sender != _mediaPlayer)
                {
                    return;
                }

                Log("info", "Playback ended" + (_nextLabel != null ? "; next: " + _nextLabel + (_binge ? " (binge watching)" : string.Empty) : string.Empty));
                PostPlayerEvent("ended", true);
                var session = _current?.Session ?? 0;
                if (_nextLabel != null && _binge)
                {
                    // The page opens the next episode's stream (or its stream list, then says 'playerIdle').
                    var label = _nextLabel;
                    StopPlayback(resetDisplay: false);
                    ShowPreparing(session, label, "Up next");
                }
                else
                {
                    StopPlayback(resetDisplay: true);
                }
            });
        }

        private void StopPlayback(bool resetDisplay)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.MediaOpened -= OnMediaOpened;
                _mediaPlayer.MediaFailed -= OnMediaFailed;
                _mediaPlayer.MediaEnded -= OnMediaEnded;
                _mediaPlayer.PlaybackSession.BufferingStarted -= OnBufferingStarted;
                _mediaPlayer.PlaybackSession.BufferingEnded -= OnBufferingEnded;
                _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
                _mediaPlayer.PlaybackSession.NaturalVideoSizeChanged -= OnNaturalVideoSizeChanged;
                Player.SetMediaPlayer(null);
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            _starting = false;
            _reopening = false;
            _hasPlayed = false;
            _scrubTarget = null;
            _scrubTimer?.Stop();
            _osdMessage = null;
            CloseMenu();
            UpNext.Visibility = Visibility.Collapsed;
            SkipIntro.Visibility = Visibility.Collapsed;
            if (!_preparing)
            {
                _progressTimer?.Stop();
                LoadingPanel.Visibility = Visibility.Collapsed;
                Player.PosterSource = null;
            }

            if (_cache != null)
            {
                // Release the read-ahead buffers now so the next playback gets the full budget
                // (0.1.3.4 got 524 MB instead of 768 MB right after a previous file).
                _cache.Dispose();
                _cache = null;
                GC.Collect();
            }

            _diagnosticsTimer?.Stop();
            _osdTimer?.Stop();
            Osd.Visibility = Visibility.Collapsed;
            PlayerLayer.Visibility = Visibility.Collapsed;
            if (_webView != null)
            {
                _webView.Visibility = Visibility.Visible;
            }

            if (_displayRequestActive)
            {
                _displayRequest.RequestRelease();
                _displayRequestActive = false;
            }

            if (resetDisplay)
            {
                _ = _displayModes.ResetAsync();
                _webView?.Focus(FocusState.Programmatic);
                ApplyCursorMode();
                if (_pendingRecovery.HasValue)
                {
                    _ = RecoverPageAsync(_pendingRecovery.Value);
                }
            }
        }

        private static string DescribeMedia(MediaPlayer player, out uint width, out uint height, out double frameRate, out uint? transferFunction, out string subtype)
        {
            subtype = string.Empty;
            width = player.PlaybackSession.NaturalVideoWidth;
            height = player.PlaybackSession.NaturalVideoHeight;
            frameRate = 0;
            transferFunction = null;
            var parts = new List<string>();

            if (player.Source is MediaPlaybackItem item)
            {
                if (item.VideoTracks.Count > 0)
                {
                    var props = item.VideoTracks[0].GetEncodingProperties();
                    subtype = props.Subtype ?? string.Empty;
                    if (props.FrameRate.Denominator != 0)
                    {
                        frameRate = (double)props.FrameRate.Numerator / props.FrameRate.Denominator;
                    }

                    if (props.Properties.TryGetValue(TransferFunctionKey, out var value) && value != null)
                    {
                        transferFunction = Convert.ToUInt32(value);
                    }

                    parts.Add(string.Format("video {0} {1}x{2} {3:0.###}fps", props.Subtype, width, height, frameRate));
                }

                parts.AddRange(item.AudioTracks.Select(t =>
                {
                    var audio = t.GetEncodingProperties();
                    return string.Format("audio {0} {1}ch [{2}]", audio.Subtype, audio.ChannelCount, t.Language);
                }));
            }

            return "Opened: " + (parts.Count == 0 ? "no track info" : string.Join(", ", parts));
        }

        private static bool IsDolbyVisionSubtype(string subtype)
        {
            var value = (subtype ?? string.Empty).ToUpperInvariant();
            return value.Contains("DVH") || value.Contains("DOVI") || value.Contains("DVAV");
        }

        private static VideoRange RangeFromTransferFunction(uint? transferFunction)
        {
            switch (transferFunction)
            {
                case 15:
                    return VideoRange.Hdr10;
                case 16:
                    return VideoRange.Hlg;
                default:
                    return VideoRange.Sdr;
            }
        }

        private void StartDiagnostics()
        {
            if (_diagnosticsTimer == null)
            {
                _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _diagnosticsTimer.Tick += (sender, e) =>
                {
                    LogPace();
                    if (++_paceTicks % 6 == 0)
                    {
                        LogPlaybackStatus("status");
                    }
                };
            }

            _paceTicks = 0;
            _paceClock = null;
            _diagnosticsTimer.Start();
        }

        // Each connection to a debrid server gets only 5-8 Mbps, so 6 connections top out near 50 Mbps: a 4K remux
        // (70-100 Mbps) drains the buffer. When the download stays under 1.3x the file's average bitrate and the
        // read-ahead window isn't full, add 2 connections (up to HttpChunkCache.MaxConnections), judged again a
        // check later; stop adding when the last ones didn't make it faster (a server limit).
        private void AdaptConnections(HttpChunkCache cache, double mbps, long ahead, double duration)
        {
            if (_fileSize <= 0 || duration <= 0 || _starting)
            {
                return;
            }

            if (ahead >= cache.AheadBytes * 0.9)
            {
                _slowChecks = 0; // Window full: the download waits for playback, so its rate says nothing.
                return;
            }

            var bitrate = _fileSize * 8.0 / duration / 1000000;
            if (mbps >= bitrate * 1.3 || _connectionsMaxed)
            {
                _slowChecks = 0;
                return;
            }

            if (++_slowChecks < 1)
            {
                return; // The check right after adding: new connections still starting.
            }

            if (_rateBeforeAdding > 0 && mbps < _rateBeforeAdding * 1.1)
            {
                _connectionsMaxed = true;
                Log("warn", string.Format("More connections didn't speed up the download ({0:0} Mbps before, {1:0} now); staying at {2}", _rateBeforeAdding, mbps, cache.Connections));
                return;
            }

            var before = cache.Connections;
            var added = cache.AddConnections(2);
            if (added == 0)
            {
                _connectionsMaxed = true;
                return;
            }

            _rateBeforeAdding = mbps;
            _slowChecks = -1;
            Log("info", string.Format("Download {0:0} Mbps for a {1:0} Mbps file: connections {2} -> {3}", mbps, bitrate, before, cache.Connections));
        }

        // Every 5 s: how far playback advanced against the wall clock (a stall shows as media < wall
        // while Playing) and the download rate from DownloadProgress x file size.
        private void LogPace()
        {
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                var position = session.Position;
                double download;
                try
                {
                    download = session.DownloadProgress;
                }
                catch (Exception)
                {
                    download = -1;
                }

                var state = session.PlaybackState;
                if (_paceClock == null)
                {
                    _paceClock = Stopwatch.StartNew();
                    _lastPosition = position;
                    _lastDownload = download;
                    return;
                }

                var wall = _paceClock.Elapsed.TotalSeconds;
                _paceClock.Restart();
                var media = (position - _lastPosition).TotalSeconds;
                var rate = "n/a";
                var cache = _cache;
                if (cache != null)
                {
                    var bytes = cache.DownloadedBytes;
                    var bytesPerSecond = (bytes - _lastCacheBytes) / wall;
                    _lastCacheBytes = bytes;
                    var ahead = cache.BufferedAheadBytes;
                    var aheadSeconds = _fileSize > 0 && session.NaturalDuration.TotalSeconds > 0 ? ahead / (_fileSize / session.NaturalDuration.TotalSeconds) : 0;
                    rate = string.Format("{0:0} Mbps, buffer {1} MB ({2:0} s), waited reads {3}", bytesPerSecond * 8 / 1000000, ahead >> 20, aheadSeconds, cache.WaitedReads);
                    if (_paceTicks > 1) // The first interval counts the whole startup download.
                    {
                        AdaptConnections(cache, bytesPerSecond * 8 / 1000000, ahead, session.NaturalDuration.TotalSeconds);
                    }
                }
                else if (_fileSize > 0 && download >= 0 && _lastDownload >= 0 && wall > 0)
                {
                    var bytesPerSecond = (download - _lastDownload) * _fileSize / wall;
                    rate = (bytesPerSecond / 1048576).ToString("0.0") + " MB/s (" + (bytesPerSecond * 8 / 1000000).ToString("0") + " Mbps)";
                }

                var stalled = state == MediaPlaybackState.Playing && media < wall * 0.9;
                Log(stalled ? "warn" : "info", string.Format(
                    "Pace{0}: {1:hh\\:mm\\:ss} +{2:0.0}s media / {3:0.0}s wall, download {4} ({5:P1}), {6}",
                    stalled ? " STALL" : string.Empty, position, media, wall, rate, download, state));
                if (cache != null)
                {
                    Log("info", cache.TakeViewSummary());
                }

                _lastPosition = position;
                _lastDownload = download;

                var duration = session.NaturalDuration.TotalSeconds;
                if (!_bitrateLogged && _fileSize > 0 && duration > 0)
                {
                    _bitrateLogged = true;
                    Log("info", string.Format("File {0:0.00} GB, average bitrate {1:0.0} Mbps", _fileSize / 1073741824.0, _fileSize * 8 / duration / 1000000));
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Pace unavailable: " + ex.GetType().Name);
            }
        }

        private void LogPlaybackStatus(string reason)
        {
            // Each value is read separately: on Xbox some PlaybackSession properties throw
            // (InvalidCastException seen in 0.1.1), and logging must never block playback control.
            try
            {
                var session = _mediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                Log("info", "Playback " + reason + ": " +
                    Read(() => session.Position.ToString(@"hh\:mm\:ss")) + " / " +
                    Read(() => session.NaturalDuration.ToString(@"hh\:mm\:ss")) +
                    ", state " + Read(() => session.PlaybackState) +
                    ", downloaded " + Read(() => session.DownloadProgress.ToString("P1")) +
                    ", memory " + Read(() => MemoryManager.AppMemoryUsage >> 20) + " / " + Read(() => MemoryManager.AppMemoryUsageLimit >> 20) + " MB");
            }
            catch (Exception ex)
            {
                Log("warn", "Playback status unavailable: " + ex.GetType().Name);
            }
        }

        private static string Read<T>(Func<T> read)
        {
            try
            {
                return Convert.ToString(read());
            }
            catch (Exception ex)
            {
                return "n/a(" + ex.GetType().Name + ")";
            }
        }

        private void OnBufferingStarted(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogPlaybackStatus("buffering started"));
        }

        private void OnBufferingEnded(MediaPlaybackSession sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => LogPlaybackStatus("buffering ended"));
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = HandleBack();
        }

        // One B press can reach both the system back event and the page; act on it once.
        private bool HandleBack()
        {
            if (unchecked(Environment.TickCount - _lastBackTick) < 500)
            {
                return true;
            }

            _lastBackTick = Environment.TickCount;
            if (_keyboardVisible)
            {
                return true; // B closes the keyboard; do not also go back.
            }

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                SetDebugVisible(false);
                return true;
            }

            if (PlayerLayer.Visibility == Visibility.Visible)
            {
                if (TrackMenu.Visibility == Visibility.Visible)
                {
                    CloseMenu();
                    return true;
                }

                if (SkipIntro.Visibility == Visibility.Visible)
                {
                    _skipIntroDismissed = true;
                    SkipIntro.Visibility = Visibility.Collapsed;
                    return true;
                }

                if (UpNext.Visibility == Visibility.Visible)
                {
                    _upNextDismissed = true;
                    UpNext.Visibility = Visibility.Collapsed;
                    return true;
                }

                if (_preparing && _mediaPlayer == null)
                {
                    Log("info", "Loading cancelled by user");
                    PostPlayerEvent("cancel", false);
                    _cancelledSession = _preparingSession;
                    _preparing = false;
                    StopPlayback(resetDisplay: true);
                    return true;
                }

                LogPlaybackStatus("stopped by user");
                PostPlayerEvent("stopped", true); // Stremio saves the position (Continue Watching).
                StopPlayback(resetDisplay: true);
                Log("info", "Playback stopped by user");
                return true;
            }

            if (_statusRetry)
            {
                return true; // Nothing behind the connection screen to go back to.
            }

            // Off the home screen, go back in Stremio's own history (WebView2's GoBack ignores pushState
            // routes). On home, leave B unhandled as before.
            if (_webView?.CoreWebView2 != null && !_pageAtHome)
            {
                _ = _webView.CoreWebView2.ExecuteScriptAsync("window.__sxBack && window.__sxBack()");
                return true;
            }

            return false;
        }

        // Keyboard shortcuts for testing on a PC. Controller input comes from GamepadInput.
        private void OnAcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args.EventType != CoreAcceleratorKeyEventType.KeyDown)
            {
                return;
            }

            // PC keyboard for the app's own screens (player, error screen, debug log). The Stremio pages
            // themselves are driven from shell.js, which gets the keys while the page has focus.
            if (!IsXbox && !_keyboardVisible &&
                (PlayerLayer.Visibility == Visibility.Visible || StatusLayer.Visibility == Visibility.Visible || DebugPanel.Visibility == Visibility.Visible))
            {
                var repeat = args.KeyStatus.WasKeyDown;
                PadButton? key = null;
                switch (args.VirtualKey)
                {
                    case VirtualKey.Up:
                        key = PadButton.Up;
                        break;
                    case VirtualKey.Down:
                        key = PadButton.Down;
                        break;
                    case VirtualKey.Left:
                        key = PadButton.Left;
                        break;
                    case VirtualKey.Right:
                        key = PadButton.Right;
                        break;
                    case VirtualKey.Enter:
                    case VirtualKey.Space:
                        key = repeat ? (PadButton?)null : PadButton.A;
                        break;
                    case VirtualKey.PageUp:
                        key = repeat ? (PadButton?)null : PadButton.LeftShoulder;
                        break;
                    case VirtualKey.PageDown:
                        key = repeat ? (PadButton?)null : PadButton.RightShoulder;
                        break;
                    case VirtualKey.Home:
                        key = PadButton.LeftTrigger;
                        break;
                    case VirtualKey.End:
                        key = PadButton.RightTrigger;
                        break;
                    case VirtualKey.Y:
                        key = repeat ? (PadButton?)null : PadButton.Y;
                        break;
                    case VirtualKey.X:
                        key = repeat ? (PadButton?)null : PadButton.X;
                        break;
                    case VirtualKey.M:
                        key = repeat ? (PadButton?)null : PadButton.Menu;
                        break;
                    case VirtualKey.Escape:
                    case VirtualKey.Back:
                        if (!repeat)
                        {
                            HandleBack();
                        }

                        args.Handled = true;
                        return;
                }

                if (key.HasValue)
                {
                    OnPadButton(key.Value);
                    args.Handled = true;
                    return;
                }
            }

            if (args.KeyStatus.WasKeyDown)
            {
                return;
            }

            switch (args.VirtualKey)
            {
                case VirtualKey.F1:
                    OnPadButton(PadButton.View);
                    args.Handled = true;
                    break;
                case VirtualKey.F2:
                    OnPadButton(PadButton.Y);
                    args.Handled = true;
                    break;
                case VirtualKey.F3:
                    OnPadButton(PadButton.X);
                    args.Handled = true;
                    break;
                case VirtualKey.F4:
                    if (!IsXbox && PlayerLayer.Visibility != Visibility.Visible)
                    {
                        PlayTestStream();
                        args.Handled = true;
                    }

                    break;
            }
        }

        // PC only (F4): play a public sample video in the native player, with a pretend intro at 0:05-0:40, to
        // try the player's controls without a Stremio streaming server or an addon that returns HTTP streams.
        private void PlayTestStream()
        {
            var message = new JsonObject
            {
                ["url"] = JsonValue.CreateStringValue("https://archive.org/download/BigBuckBunny_328/BigBuckBunny_512kb.mp4"),
                ["name"] = JsonValue.CreateStringValue("Test stream"),
                ["title"] = JsonValue.CreateStringValue("Big Buck Bunny"),
                ["subtitle"] = JsonValue.CreateStringValue("Player test (F4) · pretend intro 0:05 to 0:40"),
                ["introFrom"] = JsonValue.CreateNumberValue(5000),
                ["introTo"] = JsonValue.CreateNumberValue(40000),
                ["binge"] = JsonValue.CreateBooleanValue(false),
            };
            Log("info", "Playing the test stream (F4)");
            StartNativePlayback(PlayRequest.FromJson(message));
        }

        private void OnPadButton(PadButton button)
        {
            if (_keyboardVisible)
            {
                return; // The Xbox keyboard takes the controller.
            }

            if (button == PadButton.View)
            {
                SetDebugVisible(DebugPanel.Visibility != Visibility.Visible);
                return;
            }

            if (_statusRetry && DebugPanel.Visibility != Visibility.Visible)
            {
                if (button == PadButton.A)
                {
                    RetryPage("A pressed");
                }

                return;
            }

            if (DebugPanel.Visibility == Visibility.Visible)
            {
                if (button == PadButton.Y)
                {
                    _nativePlayerEnabled = !_nativePlayerEnabled;
                    SendConfigToPage();
                    UpdateDebugHeader();
                }
                else if (button == PadButton.X)
                {
                    SetCursorMode(!_cursorMode);
                }
                else if (button == PadButton.Right)
                {
                    _systemCursorNext = !_systemCursorNext;
                    SaveSetting("systemCursor", _systemCursorNext);
                    Log("info", "System cursor " + (_systemCursorNext ? "ON" : "OFF") + " after the app restarts");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.Left)
                {
                    var next = Array.FindIndex(ZoomSteps, step => step > _pageZoom + 0.001);
                    _pageZoom = next < 0 ? ZoomSteps[0] : ZoomSteps[next];
                    SaveSetting("pageZoom", _pageZoom);
                    _ = ApplyZoomAsync();
                    UpdateDebugHeader();
                }
                else if (button == PadButton.RightShoulder)
                {
                    _connections = _connections == 0 ? 1 : _connections == 1 ? 2 : _connections == 2 ? 4 : _connections == 4 ? 6 : _connections == 6 ? 8 : 0;
                    Log("info", "Download mode: " + DescribeConnections() + " (applies to the next playback)");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.Menu)
                {
                    _restartSeekMode = (_restartSeekMode + 1) % RestartSeekModes.Length;
                    Log("info", "Restart seek: " + DescribeRestartSeek() + " (applies to the next playback)");
                    UpdateDebugHeader();
                }
                else if (button == PadButton.LeftShoulder)
                {
                    _attachSubtitles = !_attachSubtitles;
                    Log("info", "Attach external subtitles: " + (_attachSubtitles ? "ON" : "OFF") + " (applies to the next playback)");
                    UpdateDebugHeader();
                }

                return;
            }

            if (PlayerLayer.Visibility == Visibility.Visible)
            {
                HandlePlayerButton(button);
                return;
            }

            if (_systemCursor)
            {
                // The Xbox cursor owns the left stick and A (real clicks); the D-pad still moves the highlight.
                if (button == PadButton.A || button == PadButton.LeftStick)
                {
                    return;
                }
            }
            else if (button == PadButton.LeftStick)
            {
                SetCursorMode(!_cursorMode);
                return;
            }

            if (_cursorMode)
            {
                if (button == PadButton.A)
                {
                    ClickAtCursor();
                }

                return;
            }

            switch (button)
            {
                case PadButton.Up:
                    SendNavigation("up");
                    break;
                case PadButton.Down:
                    SendNavigation("down");
                    break;
                case PadButton.Left:
                    SendNavigation("left");
                    break;
                case PadButton.Right:
                    SendNavigation("right");
                    break;
                case PadButton.A:
                    SendNavigation("activate");
                    break;
                case PadButton.Menu:
                    SendNavigation("menu"); // Options for the highlighted title (library, watched, Continue Watching).
                    break;
            }
        }

        private void SendNavigation(string action)
        {
            try
            {
                var message = new JsonObject
                {
                    ["type"] = JsonValue.CreateStringValue("nav"),
                    ["action"] = JsonValue.CreateStringValue(action),
                };
                _webView?.CoreWebView2?.PostWebMessageAsString(message.Stringify());
            }
            catch (Exception ex)
            {
                Log("error", "Could not send navigation: " + ex.Message);
            }
        }

        private void HandlePlayerButton(PadButton button)
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null || _starting)
            {
                if (button == PadButton.X && !_reopening)
                {
                    RequestStartOver();
                }

                return; // Loading screen: B cancels, X starts from the beginning instead of resuming.
            }

            if (TrackMenu.Visibility == Visibility.Visible)
            {
                HandleMenuButton(button);
                return;
            }

            // Console streaming-app layout: A play/pause (or the card on screen), D-pad seeks in 10 s steps,
            // triggers rewind / fast-forward while held, bumpers jump 10 s at once, Y / X audio & subtitles.
            switch (button)
            {
                case PadButton.Y:
                    OpenTracksPanel(AudioColumn);
                    break;
                case PadButton.X:
                    OpenTracksPanel(SubtitleColumn);
                    break;
                case PadButton.A:
                    if (SkipIntro.Visibility == Visibility.Visible)
                    {
                        SkipIntroNow();
                        break;
                    }

                    if (UpNext.Visibility == Visibility.Visible)
                    {
                        PlayNext();
                        return;
                    }

                    TogglePause(session);
                    break;
                case PadButton.Menu:
                    TogglePause(session);
                    break;
                case PadButton.Left:
                    Scrub(session, -10, accelerate: true);
                    break;
                case PadButton.Right:
                    Scrub(session, 10, accelerate: true);
                    break;
                case PadButton.LeftTrigger:
                    Scrub(session, -30, accelerate: true);
                    break;
                case PadButton.RightTrigger:
                    Scrub(session, 30, accelerate: true);
                    break;
                case PadButton.LeftShoulder:
                    Jump(session, -10);
                    break;
                case PadButton.RightShoulder:
                    Jump(session, 10);
                    break;
            }

            if (TrackMenu.Visibility != Visibility.Visible)
            {
                ShowOsd();
            }
        }

        private void TogglePause(MediaPlaybackSession session)
        {
            if (_scrubTarget.HasValue)
            {
                CommitScrub();
                _mediaPlayer.Play(); // A after seeking: play from there.
                return;
            }

            if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }

        // LB / RB: jump 10 s right away (one press, one seek).
        private void Jump(MediaPlaybackSession session, double seconds)
        {
            Scrub(session, seconds, accelerate: false);
            if (_scrubTarget.HasValue)
            {
                CommitScrub();
            }
        }

        // D-pad / trigger seeking adds up to a target shown on the bar and applied 0.7 s after the last
        // press: one seek instead of one per press, which on large files each meant a new download position.
        // Holding speeds up (x3 after 8 repeats, x6 after 24): the D-pad from 10 s steps, triggers from 30 s.
        private void Scrub(MediaPlaybackSession session, double seconds, bool accelerate)
        {
            if (!session.CanSeek)
            {
                _osdMessage = "This stream can't seek";
                return;
            }

            var now = Environment.TickCount;
            if (accelerate)
            {
                _scrubRepeats = unchecked(now - _lastScrubTick) < 250 ? _scrubRepeats + 1 : 0;
                _lastScrubTick = now;
                seconds *= _scrubRepeats >= 24 ? 6 : _scrubRepeats >= 8 ? 3 : 1;
            }

            var target = (_scrubTarget ?? session.Position) + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }

            var duration = session.NaturalDuration;
            if (duration > TimeSpan.Zero && target > duration - TimeSpan.FromSeconds(1))
            {
                target = duration - TimeSpan.FromSeconds(1);
            }

            _scrubTarget = target;
            if (_scrubTimer == null)
            {
                _scrubTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _scrubTimer.Tick += (sender, e) => CommitScrub();
            }

            _scrubTimer.Stop();
            _scrubTimer.Start();
            UpNext.Visibility = Visibility.Collapsed;
            SkipIntro.Visibility = Visibility.Collapsed;
            ShowOsd();
        }

        private void CommitScrub()
        {
            _scrubTimer?.Stop();
            var target = _scrubTarget;
            _scrubTarget = null;
            var session = _mediaPlayer?.PlaybackSession;
            if (!target.HasValue || session == null)
            {
                return;
            }

            _cache?.SeekStarted(true);
            session.Position = target.Value;
            _lastProgressTick = 0; // Report the new position on the next tick.
            Log("info", "Seek to " + FormatTime(target.Value));
            if (Osd.Visibility == Visibility.Visible)
            {
                ShowOsd();
            }
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        }

        // ---- Audio & Subtitles panel ------------------------------------------------------------------
        // One panel with two columns, like the console streaming apps: audio on the left, subtitles on the
        // right. Y opens it on audio, X on subtitles; ◀ ▶ switch columns, ▲ ▼ move, A picks, B closes.
        private sealed class MenuEntry
        {
            public MenuEntry(string label, bool selected, Action choose)
            {
                Label = label;
                Selected = selected;
                Choose = choose;
            }

            public string Label { get; }

            public bool Selected { get; }

            public Action Choose { get; }
        }

        private const int MenuWindow = 8;
        private const int AudioColumn = 0;
        private const int SubtitleColumn = 1;

        private void OpenTracksPanel(int column)
        {
            if (!(_mediaPlayer?.Source is MediaPlaybackItem item))
            {
                return;
            }

            _menuColumns[AudioColumn] = AudioEntries(item);
            _menuColumns[SubtitleColumn] = SubtitleEntries(item);
            for (var i = 0; i < _menuColumns.Length; i++)
            {
                _menuIndexes[i] = Math.Max(0, _menuColumns[i].FindIndex(entry => entry.Selected));
            }

            _menuColumn = _menuColumns[column].Count > 0 ? column : SubtitleColumn;
            TrackMenu.Visibility = Visibility.Visible;
            UpNext.Visibility = Visibility.Collapsed;
            SkipIntro.Visibility = Visibility.Collapsed;
            Osd.Visibility = Visibility.Collapsed;
            RenderMenu();
        }

        private void CloseMenu()
        {
            TrackMenu.Visibility = Visibility.Collapsed;
            _menuColumns[AudioColumn] = null;
            _menuColumns[SubtitleColumn] = null;
        }

        private void RenderMenu()
        {
            RenderColumn(AudioColumn, AudioItems, AudioHeader, AudioPosition, "No audio tracks reported");
            RenderColumn(SubtitleColumn, SubtitleItems, SubtitleHeader, SubtitlePosition, string.Empty);
        }

        private void RenderColumn(int column, StackPanel items, TextBlock header, TextBlock position, string emptyText)
        {
            var white = new SolidColorBrush(Windows.UI.Colors.White);
            var dim = new SolidColorBrush(Windows.UI.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            var soft = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
            var active = column == _menuColumn;
            var entries = _menuColumns[column];
            items.Children.Clear();
            header.Foreground = active ? white : dim;
            position.Text = string.Empty;
            if (entries == null)
            {
                return;
            }

            if (entries.Count == 0)
            {
                items.Children.Add(new TextBlock { Text = emptyText, FontSize = 20, Foreground = dim, Margin = new Thickness(16, 10, 16, 10) });
                return;
            }

            var index = _menuIndexes[column];
            var start = Math.Max(0, Math.Min(index - (MenuWindow / 2), entries.Count - MenuWindow));
            var end = Math.Min(entries.Count, start + MenuWindow);
            for (var i = start; i < end; i++)
            {
                var entry = entries[i];
                var here = i == index;
                items.Children.Add(new Border
                {
                    Padding = new Thickness(14, 9, 14, 9),
                    Margin = new Thickness(0, 0, 0, 2),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(0, 0, 0, 0),
                    // The active column's row gets a soft white highlight and a purple edge; the other column
                    // only keeps its row brighter.
                    Background = new SolidColorBrush(here && active ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    Child = new Grid
                    {
                        Children =
                        {
                            new Border
                            {
                                Width = 4,
                                CornerRadius = new CornerRadius(2),
                                Margin = new Thickness(-10, 2, 0, 2),
                                HorizontalAlignment = HorizontalAlignment.Left,
                                Background = new SolidColorBrush(here && active ? Windows.UI.Color.FromArgb(255, 0x7B, 0x5B, 0xF5) : Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                            },
                            new TextBlock
                            {
                                Text = (entry.Selected ? "✓   " : "      ") + entry.Label,
                                FontSize = 22,
                                FontWeight = here && active ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                                Foreground = here && active ? white : entry.Selected || (active && here) ? white : active ? soft : dim,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                        },
                    },
                });
            }

            position.Text = entries.Count > MenuWindow ? (index + 1) + " / " + entries.Count : string.Empty;
        }

        private void HandleMenuButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Left:
                    SwitchMenuColumn(AudioColumn);
                    break;
                case PadButton.Right:
                    SwitchMenuColumn(SubtitleColumn);
                    break;
                case PadButton.Up:
                    MoveMenu(-1);
                    break;
                case PadButton.Down:
                    MoveMenu(1);
                    break;
                case PadButton.LeftShoulder:
                case PadButton.LeftTrigger:
                    MoveMenu(-MenuWindow);
                    break;
                case PadButton.RightShoulder:
                case PadButton.RightTrigger:
                    MoveMenu(MenuWindow);
                    break;
                case PadButton.A:
                    var entries = _menuColumns[_menuColumn];
                    if (entries == null || entries.Count == 0)
                    {
                        break;
                    }

                    var entry = entries[_menuIndexes[_menuColumn]];
                    CloseMenu();
                    entry.Choose();
                    ShowOsd();
                    break;
                case PadButton.Y:
                    if (_menuColumn == AudioColumn)
                    {
                        CloseMenu();
                    }
                    else
                    {
                        SwitchMenuColumn(AudioColumn);
                    }

                    break;
                case PadButton.X:
                    if (_menuColumn == SubtitleColumn)
                    {
                        CloseMenu();
                    }
                    else
                    {
                        SwitchMenuColumn(SubtitleColumn);
                    }

                    break;
                case PadButton.Menu:
                    CloseMenu();
                    break;
            }
        }

        private void SwitchMenuColumn(int column)
        {
            if (_menuColumns[column] != null && _menuColumns[column].Count > 0 && column != _menuColumn)
            {
                _menuColumn = column;
                RenderMenu();
            }
        }

        private void MoveMenu(int delta)
        {
            var entries = _menuColumns[_menuColumn];
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            _menuIndexes[_menuColumn] = Math.Max(0, Math.Min(entries.Count - 1, _menuIndexes[_menuColumn] + delta));
            RenderMenu();
        }

        private List<MenuEntry> AudioEntries(MediaPlaybackItem item)
        {
            var entries = new List<MenuEntry>();
            try
            {
                var tracks = item.AudioTracks;
                for (var i = 0; i < tracks.Count; i++)
                {
                    var index = i;
                    var label = DescribeAudio(tracks[i]);
                    entries.Add(new MenuEntry(label, i == tracks.SelectedIndex, () => SwitchAudioTrack(item, index, label)));
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Audio tracks unavailable: " + ex.Message);
            }

            return entries;
        }

        private List<MenuEntry> SubtitleEntries(MediaPlaybackItem item)
        {
            var entries = new List<MenuEntry>();
            try
            {
                var tracks = item.TimedMetadataTracks;
                var indices = Enumerable.Range(0, tracks.Count).Where(i => IsSubtitleTrack(tracks[i])).ToList();
                var shown = indices.Where(i => tracks.GetPresentationMode((uint)i) != TimedMetadataTrackPresentationMode.Disabled).ToList();
                entries.Add(new MenuEntry("Off", shown.Count == 0, () => SelectSubtitle(item, -1, true)));
                entries.AddRange(indices.Select(index => new MenuEntry(DescribeSubtitle(tracks[index]), shown.Contains(index), () => SelectSubtitle(item, index, true))));
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle tracks unavailable: " + ex.Message);
                if (entries.Count == 0)
                {
                    entries.Add(new MenuEntry("Off", true, () => { }));
                }
            }

            return entries;
        }

        // index -1 turns subtitles off.
        private void SelectSubtitle(MediaPlaybackItem item, int index, bool byUser)
        {
            try
            {
                var tracks = item.TimedMetadataTracks;
                for (var i = 0; i < tracks.Count; i++)
                {
                    if (i != index && IsSubtitleTrack(tracks[i]))
                    {
                        var before = tracks.GetPresentationMode((uint)i);
                        if (before != TimedMetadataTrackPresentationMode.Disabled && !byUser)
                        {
                            Log("info", "Subtitle track was on before the app's choice: " + DescribeSubtitle(tracks[i]) + " (" + before + "); turning it off");
                        }

                        tracks.SetPresentationMode((uint)i, TimedMetadataTrackPresentationMode.Disabled);
                    }
                }

                if (index >= 0)
                {
                    tracks.SetPresentationMode((uint)index, TimedMetadataTrackPresentationMode.PlatformPresented);
                }

                if (byUser)
                {
                    _subtitleUserChoice = true;
                    _osdMessage = index >= 0 ? "Subtitles: " + DescribeSubtitle(tracks[index]) : "Subtitles: off";
                }

                Log("info", (byUser ? "Subtitles: " : "Subtitles (Stremio's language setting): ") + (index >= 0 ? DescribeSubtitle(tracks[index]) : "off"));
            }
            catch (Exception ex)
            {
                _osdMessage = "Subtitle switch failed";
                Log("warn", "Subtitle switch failed: " + ex.Message);
            }
        }

        // Like Stremio: turn on subtitles in the language set in Stremio's settings, once, unless the viewer
        // already chose; for a foreign title playing in its own language, English ones. Embedded tracks
        // resolve first; addon subtitles follow as their files download. Forced tracks (only the lines in
        // another language) are skipped: they are not full subtitles.
        private void AutoSelectSubtitles(MediaPlaybackItem item)
        {
            if (_restoreSubtitle != null && _mediaPlayer?.Source == item)
            {
                // After an audio switch: the subtitle that was on (external ones resolve one by one).
                if (_restoreSubtitle.Length == 0)
                {
                    _restoreSubtitle = null;
                    return;
                }

                var all = item.TimedMetadataTracks;
                for (var i = 0; i < all.Count; i++)
                {
                    if (IsSubtitleTrack(all[i]) && DescribeSubtitle(all[i]) == _restoreSubtitle)
                    {
                        _restoreSubtitle = null;
                        SelectSubtitle(item, i, false);
                        return;
                    }
                }

                return;
            }

            if (_subtitleAutoDone || _subtitleUserChoice || _current == null || _mediaPlayer?.Source != item || !_audioDecided)
            {
                return;
            }

            var language = _originalAudio ? "en" : _current.SubtitlesLanguage;
            if (string.IsNullOrEmpty(language))
            {
                _subtitleAutoDone = true;
                return;
            }

            if (!_originalAudio && LanguageMatches(_audioLanguage, language))
            {
                // Subtitles in the language already spoken: off by default (X still turns them on).
                _subtitleAutoDone = true;
                Log("info", "Subtitles off by default: the audio is already " + LanguageName(language));
                return;
            }

            try
            {
                // A downloaded subtitle in the language first; one inside the video file only when none comes.
                var tracks = item.TimedMetadataTracks;
                var external = -1;
                var embedded = -1;
                for (var i = 0; i < tracks.Count; i++)
                {
                    if (!IsSubtitleTrack(tracks[i]) || IsForcedSubtitle(tracks[i]))
                    {
                        continue;
                    }

                    if (IsExternalSubtitle(tracks[i], out var externalLanguage))
                    {
                        if (external < 0 && LanguageMatches(externalLanguage, language))
                        {
                            external = i;
                        }
                    }
                    else if (embedded < 0 && LanguageMatches(tracks[i].Language, language))
                    {
                        embedded = i;
                    }
                }

                var expectExternal = _externalSubtitlesPending > 0 &&
                    _externalSubtitleLanguages.Values.Any(code => LanguageMatches(code, language));
                if (external < 0 && embedded >= 0 && expectExternal && !_embeddedSubtitleAllowed)
                {
                    WaitForExternalSubtitles(item); // Falls back to the one in the file after a while.
                    return;
                }

                var pick = external >= 0 ? external : embedded;
                if (pick >= 0)
                {
                    _subtitleAutoDone = true;
                    SelectSubtitle(item, pick, false);
                    Log("info", "Subtitles chosen: " + DescribeSubtitle(tracks[pick]) + (external >= 0
                        ? (embedded >= 0 ? " (downloaded, preferred over the one in the file)" : " (downloaded)")
                        : " (in the file; no downloaded one in this language)"));
                    if (_originalAudio)
                    {
                        AddOsdNote("English subtitles   ·   X to change");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("warn", "Subtitle auto-select failed: " + ex.Message);
            }
        }

        private bool IsExternalSubtitle(TimedMetadataTrack track, out string language)
        {
            language = null;
            return !string.IsNullOrEmpty(track.Label) && _externalSubtitleLanguages.TryGetValue(track.Label, out language);
        }

        // Downloaded subtitles load one by one after the file opens; give them a few seconds before settling
        // for the subtitle inside the file.
        private async void WaitForExternalSubtitles(MediaPlaybackItem item)
        {
            if (_subtitleWaiting)
            {
                return;
            }

            _subtitleWaiting = true;
            var generation = _subtitleGeneration;
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (generation != _subtitleGeneration || _mediaPlayer?.Source != item)
            {
                return;
            }

            _subtitleWaiting = false;
            if (!_subtitleAutoDone && !_subtitleUserChoice)
            {
                Log("info", "No downloaded subtitle in the language yet; using the one in the file");
                _embeddedSubtitleAllowed = true;
                AutoSelectSubtitles(item);
            }
        }

        private static bool IsForcedSubtitle(TimedMetadataTrack track)
        {
            return (track.Label ?? string.Empty).IndexOf("forced", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Stremio uses ISO 639-2 codes ("eng", "ger"); files and addons use 639-1 or 639-2 ("en", "deu").
        private static readonly Dictionary<string, string> Iso6392To6391 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "en", ["spa"] = "es", ["fre"] = "fr", ["fra"] = "fr", ["ger"] = "de", ["deu"] = "de", ["ita"] = "it",
            ["por"] = "pt", ["pob"] = "pt", ["rus"] = "ru", ["jpn"] = "ja", ["kor"] = "ko", ["chi"] = "zh", ["zho"] = "zh",
            ["ara"] = "ar", ["hin"] = "hi", ["tur"] = "tr", ["pol"] = "pl", ["dut"] = "nl", ["nld"] = "nl", ["swe"] = "sv",
            ["nor"] = "no", ["nob"] = "no", ["dan"] = "da", ["fin"] = "fi", ["gre"] = "el", ["ell"] = "el", ["heb"] = "he",
            ["hun"] = "hu", ["cze"] = "cs", ["ces"] = "cs", ["rum"] = "ro", ["ron"] = "ro", ["bul"] = "bg", ["ukr"] = "uk",
            ["vie"] = "vi", ["tha"] = "th", ["ind"] = "id", ["may"] = "ms", ["msa"] = "ms", ["hrv"] = "hr", ["srp"] = "sr",
            ["slv"] = "sl", ["slo"] = "sk", ["slk"] = "sk", ["per"] = "fa", ["fas"] = "fa", ["tam"] = "ta", ["tel"] = "te",
            ["ben"] = "bn", ["mal"] = "ml", ["est"] = "et", ["lav"] = "lv", ["lit"] = "lt", ["ice"] = "is", ["isl"] = "is",
            ["kan"] = "kn", ["mar"] = "mr", ["pan"] = "pa", ["guj"] = "gu", ["tgl"] = "tl", ["fil"] = "tl", ["nno"] = "nn",
        };

        private static string NormalizeLanguage(string code)
        {
            var value = (code ?? string.Empty).Trim().ToLowerInvariant();
            var dash = value.IndexOfAny(new[] { '-', '_' });
            if (dash > 0)
            {
                value = value.Substring(0, dash);
            }

            return Iso6392To6391.TryGetValue(value, out var shortCode) ? shortCode : value;
        }

        private static bool LanguageMatches(string trackLanguage, string preferred)
        {
            var a = NormalizeLanguage(trackLanguage);
            var b = NormalizeLanguage(preferred);
            return a.Length > 0 && a != "und" && a == b;
        }

        private static string LanguageName(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || NormalizeLanguage(code) == "und")
            {
                return "Unknown";
            }

            try
            {
                var tag = NormalizeLanguage(code);
                if (Windows.Globalization.Language.IsWellFormed(tag))
                {
                    var name = new Windows.Globalization.Language(tag).DisplayName;
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            catch (Exception)
            {
            }

            return code;
        }

        private static string AudioFormatName(AudioTrack track)
        {
            var props = track.GetEncodingProperties();
            var subtype = (props.Subtype ?? string.Empty).ToUpperInvariant();
            return IsTrueHd(track) ? "Dolby TrueHD"
                : IsDtsHd(track) ? "DTS-HD"
                : subtype.Contains("EAC3") || subtype.Contains("E-AC3") || subtype.Contains("DDPLUS") ? "Dolby Digital Plus"
                : subtype.Contains("AC3") ? "Dolby Digital"
                : subtype.Contains("DTS") ? "DTS"
                : subtype.Length > 0 ? props.Subtype
                : "Audio";
        }

        private static string DescribeAudio(AudioTrack track)
        {
            var props = track.GetEncodingProperties();
            var format = AudioFormatName(track);
            var channels = props.ChannelCount == 8 ? "7.1" : props.ChannelCount == 6 ? "5.1" : props.ChannelCount == 2 ? "Stereo" : props.ChannelCount == 1 ? "Mono" : props.ChannelCount + " ch";
            var label = string.IsNullOrEmpty(track.Label) ? string.Empty : "  ·  " + track.Label;
            // An untagged track is just its format ("AAC Stereo"), not "Unknown · AAC Stereo".
            var untagged = string.IsNullOrWhiteSpace(track.Language) || NormalizeLanguage(track.Language) == "und";
            return (untagged ? string.Empty : LanguageName(track.Language) + "  ·  ") + format + " " + channels + label;
        }

        private void AddExternalSubtitles(MediaSource source, PlayRequest request)
        {
            _externalSubtitleLanguages.Clear();
            _externalSubtitlesPending = 0;
            foreach (var subtitle in request.Subtitles)
            {
                try
                {
                    var label = string.IsNullOrEmpty(subtitle.Label) ? subtitle.Language : subtitle.Label;
                    var language = subtitle.Language;
                    var timedText = TimedTextSource.CreateFromUri(new Uri(subtitle.Url));
                    timedText.Resolved += (sender, args) =>
                    {
                        var failed = args.Error != null;
                        var error = failed ? args.Error.ErrorCode.ToString() : null;
                        if (!failed)
                        {
                            foreach (var track in args.Tracks)
                            {
                                track.Label = label;
                            }
                        }

                        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            if (_externalSubtitlesPending > 0)
                            {
                                _externalSubtitlesPending--;
                            }

                            if (failed)
                            {
                                Log("warn", "Subtitle failed (" + label + "): " + error);
                            }

                            // The last one in the language may have failed, or this one is the first to arrive.
                            if (_mediaPlayer?.Source is MediaPlaybackItem current)
                            {
                                AutoSelectSubtitles(current);
                            }
                        });
                    };
                    source.ExternalTimedTextSources.Add(timedText);
                    if (!_externalSubtitleLanguages.ContainsKey(label))
                    {
                        _externalSubtitleLanguages[label] = language;
                    }

                    _externalSubtitlesPending++;
                }
                catch (Exception ex)
                {
                    Log("warn", "Subtitle skipped (" + subtitle.Label + "): " + ex.Message);
                }
            }

            Log("info", "External subtitles: " + request.Subtitles.Count);
        }

        private static bool IsSubtitleTrack(TimedMetadataTrack track)
        {
            return track.TimedMetadataKind == TimedMetadataKind.Subtitle ||
                   track.TimedMetadataKind == TimedMetadataKind.Caption ||
                   track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle;
        }

        private static string DescribeSubtitle(TimedMetadataTrack track)
        {
            var label = string.IsNullOrEmpty(track.Label) ? (track.Language + " (embedded)") : track.Label;
            return track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle ? label + " [image]" : label;
        }

        private void LogSubtitleTracks(MediaPlaybackItem item)
        {
            try
            {
                var tracks = item.TimedMetadataTracks.Where(IsSubtitleTrack).Select(DescribeSubtitle).ToList();
                Log("info", "Subtitle tracks: " + tracks.Count + (tracks.Count > 0 ? " - " + string.Join(", ", tracks.Take(15)) : string.Empty));
            }
            catch (Exception ex)
            {
                Log("warn", "Could not list subtitle tracks: " + ex.Message);
            }
        }

        private void ShowOsd()
        {
            if (_osdTimer == null)
            {
                _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _osdTimer.Tick += (sender, e) => UpdateOsd();
            }

            _osdHideAt = DateTime.UtcNow.AddSeconds(4);
            Osd.Visibility = Visibility.Visible;
            UpdateOsd();
            _osdTimer.Start();
        }

        private void UpdateOsd()
        {
            var session = _mediaPlayer?.PlaybackSession;
            if (session == null || TrackMenu.Visibility == Visibility.Visible)
            {
                _osdTimer?.Stop();
                Osd.Visibility = Visibility.Collapsed;
                SetCenterIndicator(null);
                return;
            }

            var state = session.PlaybackState;
            var paused = state != MediaPlaybackState.Playing;
            if (!paused && !_scrubTarget.HasValue && DateTime.UtcNow >= _osdHideAt)
            {
                _osdTimer.Stop();
                _osdMessage = null;
                Osd.Visibility = Visibility.Collapsed;
                return;
            }

            var duration = session.NaturalDuration;
            var position = session.Position;
            OsdTitle.Text = _current?.DisplayTitle ?? string.Empty;
            OsdSubtitle.Text = _current?.DisplaySubtitle ?? string.Empty;
            OsdSubtitle.Visibility = string.IsNullOrEmpty(OsdSubtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
            OsdInfo.Text = _osdMessage ?? string.Empty;
            OsdInfo.Visibility = string.IsNullOrEmpty(_osdMessage) ? Visibility.Collapsed : Visibility.Visible;

            var buffering = state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening;
            string status;
            if (_scrubTarget.HasValue)
            {
                var delta = _scrubTarget.Value - position;
                position = _scrubTarget.Value;
                status = "Seek " + (delta < TimeSpan.Zero ? "−" : "+") + FormatTime(delta.Duration());
            }
            else
            {
                status = !paused ? "Playing" : buffering ? "Buffering" : "Paused";
            }

            // Seek bar: the played part and a knob that grows while seeking.
            var fraction = duration.TotalSeconds > 0 ? Math.Max(0, Math.Min(1, position.TotalSeconds / duration.TotalSeconds)) : 0;
            var width = OsdTrack.ActualWidth;
            var knob = _scrubTarget.HasValue ? 26 : 16;
            OsdFill.Width = width * fraction;
            OsdKnob.Width = knob;
            OsdKnob.Height = knob;
            OsdKnobShift.X = (width * fraction) - (knob / 2.0);
            OsdTime.Text = duration > TimeSpan.Zero
                ? string.Format("{0}   ·   {1} / {2}", status, FormatTime(position), FormatTime(duration))
                : status + "   ·   " + FormatTime(position);
            OsdRemaining.Text = duration > TimeSpan.Zero ? "−" + FormatTime(duration - position < TimeSpan.Zero ? TimeSpan.Zero : duration - position) : string.Empty;
            SetCenterIndicator(_scrubTarget.HasValue || LoadingPanel.Visibility == Visibility.Visible ? null : buffering ? "buffering" : paused ? "paused" : null);
        }

        // The round indicator in the middle of the picture: "paused", "buffering" or null (hidden).
        private void SetCenterIndicator(string kind)
        {
            CenterIndicator.Visibility = kind == null ? Visibility.Collapsed : Visibility.Visible;
            CenterGlyph.Visibility = kind == "paused" ? Visibility.Visible : Visibility.Collapsed;
            CenterRing.Visibility = kind == "buffering" ? Visibility.Visible : Visibility.Collapsed;
            CenterRing.IsActive = kind == "buffering";
        }

        // Skip intro / Up Next: in the bottom corner, lifted just above the bar while the bar is showing.
        private void PositionCards()
        {
            var lift = Osd.Visibility == Visibility.Visible && Osd.ActualHeight > 0 ? -Math.Max(0, Osd.ActualHeight - 150) : 0;
            Slide(SkipIntroShift, lift);
            Slide(UpNextShift, lift);
        }

        private static void Slide(TranslateTransform shift, double to)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, shift);
            Storyboard.SetTargetProperty(animation, "Y");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        // Overlays fade in and out instead of popping (composition animations, run off the UI thread).
        private void SetUpFades()
        {
            try
            {
                var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
                Windows.UI.Composition.ScalarKeyFrameAnimation Fade(float from, float to, int milliseconds)
                {
                    var animation = compositor.CreateScalarKeyFrameAnimation();
                    animation.Target = "Opacity";
                    animation.InsertKeyFrame(0f, from);
                    animation.InsertKeyFrame(1f, to);
                    animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
                    return animation;
                }

                foreach (var element in new UIElement[] { Osd, TrackMenu, SkipIntro, UpNext, CenterIndicator, Toast, LoadingPanel })
                {
                    ElementCompositionPreview.SetImplicitShowAnimation(element, Fade(0f, 1f, 220));
                    ElementCompositionPreview.SetImplicitHideAnimation(element, Fade(1f, 0f, 180));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Fades unavailable: " + ex.Message);
            }
        }

        private void SetDebugVisible(bool visible)
        {
            DebugPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDebugHeader()
        {
            DebugHeader.Text = "Debug log  |  View: hide  |  Y: native player " + (_nativePlayerEnabled ? "ON" : "OFF") +
                "  |  X: cursor " + (_cursorMode ? "ON" : "OFF") + "  |  Right: system cursor " + (_systemCursorNext ? "ON" : "OFF") + (_systemCursorNext != _systemCursor ? " (restart)" : string.Empty) + "  |  Left: zoom " + Math.Round(_pageZoom * 100) + "%" +
                "  |  LB: subtitles " + (_attachSubtitles ? "ON" : "OFF") + "  |  RB: " + DescribeConnections() +
                "  |  Menu: seek " + DescribeRestartSeek() + "  |  B: close";
        }

        private string DescribeConnections()
        {
            return _connections == 0 ? "direct URL" : _connections + " connection" + (_connections == 1 ? string.Empty : "s");
        }

        private void LogAsync(string level, string text)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Log(level, text));
        }

        private void Log(string level, string text)
        {
            FileLog.Write(level, text);
            var line = DateTime.Now.ToString("HH:mm:ss") + " " + level.ToUpperInvariant() + " " + text;

            _log.Add(line.Length > 600 ? line.Substring(0, 600) + "..." : line);
            if (_log.Count > 500)
            {
                _log.RemoveRange(0, _log.Count - 500);
            }

            if (DebugText != null)
            {
                DebugText.Text = string.Join("\n", _log.Skip(Math.Max(0, _log.Count - VisibleLogLines)));
            }
        }
    }
}
