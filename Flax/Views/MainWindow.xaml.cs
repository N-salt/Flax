using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using Microsoft.Win32;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Foundation.Collections;

namespace Flax
{
    /// <summary>
    /// 앱 설정을 저장/로드하는 클래스
    /// </summary>
    public class AppSettings
    {
        public bool AutoStart { get; set; } = false;

        public bool MinimizeToTray { get; set; } = false;
        public int CheckIntervalMinutes { get; set; } = 5;

        /// <summary>
        /// 쇼케이스 모드 활성화 여부 (개인정보 보호 + 성능 최적화)
        /// </summary>
        public bool ShowcaseMode { get; set; } = false;

        private static readonly string SETTINGS_FILE_PATH = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Flax",
            "settings.json"
        );

        /// <summary>
        /// 설정 로드
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(SETTINGS_FILE_PATH);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory!);

                if (File.Exists(SETTINGS_FILE_PATH))
                {
                    var json = File.ReadAllText(SETTINGS_FILE_PATH);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        if (settings != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AppSettings] 설정 로드 완료 - AutoStart: {settings.AutoStart}, MinimizeToTray: {settings.MinimizeToTray}, CheckInterval: {settings.CheckIntervalMinutes}분");
                            return settings;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] 설정 로드 실패: {ex.Message}");
            }

            // 기본값 반환
            var defaultSettings = new AppSettings();
            System.Diagnostics.Debug.WriteLine($"[AppSettings] 기본 설정 사용");
            return defaultSettings;
        }

        /// <summary>
        /// 설정 저장
        /// </summary>
        public void Save()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(SETTINGS_FILE_PATH);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory!);

                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SETTINGS_FILE_PATH, json);
                System.Diagnostics.Debug.WriteLine($"[AppSettings] 설정 저장 완료 - AutoStart: {AutoStart}, MinimizeToTray: {MinimizeToTray}, CheckInterval: {CheckIntervalMinutes}분");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings] 설정 저장 실패: {ex.Message}");
            }
        }
    }

    public partial class MainWindow : Window
    {
        private bool _isRefreshing = false;
        private int _animationDelayCounter = 0;
        private Storyboard? _loadingStoryboard;
        private Storyboard? _backgroundGradientStoryboard;
        private bool _isSidebarExpanded = false;
        private const double SIDEBAR_COLLAPSED_WIDTH = 70;
        private const double SIDEBAR_EXPANDED_WIDTH = 220;
        private List<StreamItem>? _cachedData;
        private DateTime _lastFetchTime = DateTime.MinValue;
        private const int CACHE_DURATION_SECONDS = 30;
        private bool _isSearching = false;
        private System.Windows.Threading.DispatcherTimer? _tipTimer;
        private int _currentTipIndex = 0;
        private bool _isLoadingSettings = false;
        private readonly string[] _loadingTips = {
            "Tip: 처음 켜질 때의 로딩이 다소 길 수 있습니다.",
            "Tip: 스트리머 이름을 클릭하면 방송으로 바로 이동합니다.",
            "Tip: 우측 상단의 새로고침 버튼으로 최신 순위를 확인하세요.",
            "Tip: Flax는 치지직과 SOOP의 데이터를 실시간으로 수집합니다.",
            "Tip: 시청자 수는 실시간으로 업데이트되어 빨간색으로 표시됩니다.",
            "Tip: 네트워크 환경에 따라 로딩 속도가 달라질 수 있습니다.",
            "Tip: SOOP 방송은 간헐적으로 수집되지 않을 수 있습니다.",
        };
        private FollowLiveChecker? _liveChecker;
        private TrayIconManager? _trayIconManager;
        private bool _isInTrayMode = false;
        private WindowState _previousWindowState = WindowState.Normal;
        private bool _isFullscreen = false;
        private WindowStyle _previousWindowStyle;
        private double _previousWidth;
        private double _previousHeight;
        private double _previousLeft;
        private double _previousTop;
        private AppSettings _appSettings = new AppSettings(); // [추가] 앱 설정

        // [쇼케이스 모드] 가명 생성용 랜덤 시드
        private Random _showcaseRandom = new Random(12345); // 고정 시드로 일관된 가명 생성

        public MainWindow()
        {
            // [핵심 수정 1] 제일 먼저 로딩 플래그 설정 (InitializeComponent 전에!)
            _isLoadingSettings = true;

            // [핵심 수정 2] 설정 로드 (UI 초기화 전에!)
            _appSettings = AppSettings.Load();
            System.Diagnostics.Debug.WriteLine($"[생성자] 설정 로드 완료 - AutoStart: {_appSettings.AutoStart}, MinimizeToTray: {_appSettings.MinimizeToTray}, CheckInterval: {_appSettings.CheckIntervalMinutes}분");

            // [핵심 수정 3] UI 초기화 (이때 XAML 기본값으로 이벤트 발생하지만 플래그로 차단됨)
            InitializeComponent();

            // [핵심 수정 4] UI 초기화 직후 설정값 적용
            ApplySettingsToUI();

            // [핵심 수정 5] 플래그 해제
            _isLoadingSettings = false;

            this.Loaded += MainWindow_Loaded;
            ToastNotificationManagerCompat.OnActivated += OnToastNotificationActivated;

            // 커맨드라인 인자 확인 (백그라운드 시작)
            CheckStartupMode();
        }

        /// <summary>
        /// 설정값을 UI에 적용 (이벤트 발생 방지)
        /// </summary>
        private void ApplySettingsToUI()
        {
            try
            {
                if (ChkAutoStart != null)
                    ChkAutoStart.IsChecked = _appSettings.AutoStart;

                if (ChkMinimizeToTray != null)
                    ChkMinimizeToTray.IsChecked = _appSettings.MinimizeToTray;

                if (SliderCheckInterval != null)
                    SliderCheckInterval.Value = _appSettings.CheckIntervalMinutes;

                if (ChkShowcaseMode != null)
                    ChkShowcaseMode.IsChecked = _appSettings.ShowcaseMode;

                System.Diagnostics.Debug.WriteLine($"[ApplySettingsToUI] UI에 설정 적용 완료");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplySettingsToUI] UI 설정 적용 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 시작 모드 체크 (백그라운드 시작 여부)
        /// </summary>
        private void CheckStartupMode()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1] == "--background")
            {
                // 백그라운드 모드로 시작
                _isInTrayMode = true;
                this.WindowState = WindowState.Minimized;
                this.ShowInTaskbar = false;
                LogInfo("백그라운드 모드로 시작");
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartBackgroundGradientAnimation();

            await InitWebView();
            InitializeFollowSystem();
            InitializeTrayIcon();

            // [변경] 메인화면을 먼저 로딩
            if (!_isInTrayMode)
            {
                LogInfo("메인화면 먼저 로딩 시작");
                RefreshData();
            }

            // [변경] 메인화면 로딩 후 팔로우 체크 시작
            Task.Run(async () =>
            {
                // WebView 초기화와 메인화면 로딩 완료 대기
                await Task.Delay(3000);
                LogInfo("팔로우 스트리머 체크 시작 (메인화면 로딩 후)");
                await _liveChecker?.CheckImmediatelyAsync()!;
                LogInfo("팔로우 스트리머 체크 완료");
            });

            if (_isInTrayMode)
            {
                // 백그라운드 모드에서는 창 숨김
                this.Hide();
            }
        }

        private async Task InitWebView()
        {
            try
            {
                if (SoopWebView != null)
                {
                    await SoopWebView.EnsureCoreWebView2Async();
                    if (SoopWebView.CoreWebView2 != null)
                        SoopWebView.CoreWebView2.IsMuted = true;
                }
            }
            catch (Exception ex)
            {
                LogError("WebView 초기화 실패", ex);
            }
        }

        #region 로깅
        private void LogError(string message, Exception? ex = null)
        {
            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (ex != null)
                logMessage += $"\n예외: {ex.Message}\n스택: {ex.StackTrace}";
            Debug.WriteLine(logMessage);
        }

        private void LogInfo(string message)
        {
            Debug.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}");
        }

        /// <summary>
        /// 쇼케이스 모드용 가명 생성
        /// </summary>
        private string GeneratePseudonym(string originalName, string platform)
        {
            if (!_appSettings.ShowcaseMode)
                return originalName;

            // 원본 이름 해시로 시드 생성 (일관된 가명 생성)
            int seed = (originalName + platform).GetHashCode();
            var rng = new Random(seed);

            string[] prefixes = { "스트리머", "방송인", "크리에이터", "BJ", "스트림" };
            string prefix = prefixes[rng.Next(prefixes.Length)];
            int number = rng.Next(1, 1000);

            return $"{prefix}{number}";
        }

        /// <summary>
        /// 쇼케이스 모드용 회색 플레이스홀더 이미지 생성
        /// </summary>
        private BitmapImage GetShowcasePlaceholderImage()
        {
            // 회색 원형 프로필 이미지 (간단한 PNG)
            byte[] grayCircle =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
                0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x20,
                0x08, 0x02, 0x00, 0x00, 0x00, 0xFC, 0x18, 0xED, 0xA3, 0x00, 0x00, 0x00,
                0x19, 0x74, 0x45, 0x58, 0x74, 0x53, 0x6F, 0x66, 0x74, 0x77, 0x61, 0x72,
                0x65, 0x00, 0x41, 0x64, 0x6F, 0x62, 0x65, 0x20, 0x49, 0x6D, 0x61, 0x67,
                0x65, 0x52, 0x65, 0x61, 0x64, 0x79, 0x71, 0xC9, 0x65, 0x3C, 0x00, 0x00,
                0x00, 0x18, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x62, 0x64, 0x64, 0x64,
                0xF8, 0xCF, 0x40, 0x05, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00,
                0x00, 0x0D, 0x00, 0x03, 0xB8, 0x1C, 0x2B, 0x01, 0x00, 0x00, 0x00, 0x00,
                0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
            };

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new System.IO.MemoryStream(grayCircle);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        #endregion

        #region 사이드바
        private void Sidebar_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isSidebarExpanded)
            {
                _isSidebarExpanded = true;
                AnimateSidebar(SIDEBAR_EXPANDED_WIDTH, "FLAX");
            }
        }

        private void Sidebar_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isSidebarExpanded)
            {
                _isSidebarExpanded = false;
                AnimateSidebar(SIDEBAR_COLLAPSED_WIDTH, "F");
            }
        }

        private void AnimateSidebar(double targetWidth, string logoText)
        {
            var widthAnimation = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Sidebar.BeginAnimation(WidthProperty, widthAnimation);
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fadeOut.Completed += (s, e) =>
            {
                SidebarLogo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            };
            SidebarLogo.BeginAnimation(OpacityProperty, fadeOut);
        }
        #endregion

        #region 페이지 전환
        private void BtnHome_Click(object sender, RoutedEventArgs e) => NavigateToPage("home");
        private void BtnSearch_Click(object sender, RoutedEventArgs e) => NavigateToPage("search");
        private void BtnFollow_Click(object sender, RoutedEventArgs e) => NavigateToPage("follow");
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => NavigateToPage("settings");

        private void NavigateToPage(string pageName)
        {
            MainPage.Visibility = Visibility.Collapsed;
            SearchPage.Visibility = Visibility.Collapsed;
            FollowPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            BtnHome.Style = (Style)FindResource("SidebarButtonStyle");
            BtnSearch.Style = (Style)FindResource("SidebarButtonStyle");
            BtnFollow.Style = (Style)FindResource("SidebarButtonStyle");
            BtnSettings.Style = (Style)FindResource("SidebarButtonStyle");

            switch (pageName.ToLower())
            {
                case "home":
                    MainPage.Visibility = Visibility.Visible;
                    BtnHome.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    PageTitle.Text = "FLAX";
                    PageSubtitle.Text = "통합 방송 시청 플랫폼";
                    BtnRefresh.Visibility = Visibility.Visible;
                    break;
                case "search":
                    SearchPage.Visibility = Visibility.Visible;
                    BtnSearch.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    PageTitle.Text = "검색";
                    PageSubtitle.Text = "스트리머와 방송을 찾아보세요";
                    BtnRefresh.Visibility = Visibility.Collapsed;
                    TxtSearch.Focus();
                    break;
                case "follow":
                    FollowPage.Visibility = Visibility.Visible;
                    BtnFollow.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    PageTitle.Text = "팔로우";
                    PageSubtitle.Text = "즐겨찾는 스트리머의 방송";
                    BtnRefresh.Visibility = Visibility.Collapsed;
                    LoadFollowPage();
                    break;
                case "settings":
                    SettingsPage.Visibility = Visibility.Visible;
                    BtnSettings.Style = (Style)FindResource("ActiveSidebarButtonStyle");
                    PageTitle.Text = "설정";
                    PageSubtitle.Text = "앱 환경설정";
                    BtnRefresh.Visibility = Visibility.Collapsed;
                    LoadSettings();
                    break;
            }
            LogInfo($"페이지 전환: {pageName}");
        }
        #endregion

        #region 검색 (기존 코드 유지)
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtSearchPlaceholder != null)
                TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ExecuteSearch();
        }

        private void BtnSearchExecute_Click(object sender, RoutedEventArgs e) => ExecuteSearch();

        private async void ExecuteSearch()
        {
            string keyword = TxtSearch.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(keyword) || _isSearching) return;

            _isSearching = true;
            SetLoading(true);

            try
            {
                LogInfo($"검색 시작: {keyword}");
                var chzzkTask = SearchChzzkChannels(keyword);
                var soopTask = SearchSoopChannels(keyword);
                await Task.WhenAll(chzzkTask, soopTask);
                var allResults = chzzkTask.Result.Concat(soopTask.Result).ToList();
                LogInfo($"검색 완료 - 치지직: {chzzkTask.Result.Count}개, SOOP: {soopTask.Result.Count}개");
                DisplaySearchResults(allResults);
            }
            catch (Exception ex)
            {
                LogError("검색 중 오류 발생", ex);
                ShowErrorMessage("검색 중 오류가 발생했습니다.");
            }
            finally
            {
                SetLoading(false);
                _isSearching = false;
            }
        }

        private async Task<List<StreamerInfo>> SearchChzzkChannels(string keyword)
        {
            var list = new List<StreamerInfo>();
            try
            {
                var encodedKeyword = Uri.EscapeDataString(keyword);
                var options = new RestClientOptions($"https://api.chzzk.naver.com/service/v1/search/channels?keyword={encodedKeyword}&offset=0&size=20")
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };
                var client = new RestClient(options);
                var response = await client.ExecuteAsync(new RestRequest());

                if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    var json = JObject.Parse(response.Content);
                    var data = json["content"]?["data"];
                    if (data != null)
                    {
                        foreach (var item in data)
                        {
                            var channelId = item["channel"]?["channelId"]?.ToString() ?? "";
                            list.Add(new StreamerInfo
                            {
                                Platform = "치지직",
                                StreamerId = channelId,
                                StreamerName = item["channel"]?["channelName"]?.ToString() ?? "",
                                ProfileImageUrl = item["channel"]?["channelImageUrl"]?.ToString() ?? "",
                                Description = item["channel"]?["channelDescription"]?.ToString() ?? "",
                                FollowerCount = item["channel"]?["followerCount"]?.Value<int>() ?? 0,
                                IsLive = item["channel"]?["openLive"]?.Value<bool>() ?? false,
                                ChannelUrl = $"https://chzzk.naver.com/{channelId}"
                            });
                            Debug.WriteLine($"치지직 검색 결과 추가: {channelId}");
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("치지직 채널 검색 실패", ex); }
            return list;
        }

        private async Task<List<StreamerInfo>> SearchSoopChannels(string keyword)
        {
            var list = new List<StreamerInfo>();
            if (SoopWebView?.CoreWebView2 == null) return list;

            try
            {
                var encodedKeyword = Uri.EscapeDataString(keyword);
                var searchUrl = $"https://www.sooplive.co.kr/search?szLocation=total_search&szSearchType=streamer&szKeyword={encodedKeyword}&szStype=di&szActype=input_field";
                var tcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
                handler = (s, e) =>
                {
                    if (handler != null && SoopWebView?.CoreWebView2 != null)
                        SoopWebView.CoreWebView2.NavigationCompleted -= handler;
                    tcs.TrySetResult(e.IsSuccess);
                };
                SoopWebView.CoreWebView2.NavigationCompleted += handler;
                SoopWebView.CoreWebView2.Navigate(searchUrl);

                if (await Task.WhenAny(tcs.Task, Task.Delay(8000)) == tcs.Task && await tcs.Task)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(500);
                        var itemCount = await SoopWebView.ExecuteScriptAsync("document.querySelectorAll('#container > div.search_strm_area > ul > li').length");
                        if (itemCount != null && itemCount != "0") break;
                    }

                    string script = @"(function(){
                var items = document.querySelectorAll('#container > div.search_strm_area > ul > li');
                var results = [];
                for(var i=0; i<Math.min(items.length, 20); i++){
                    var it = items[i];
                    try {
                        var nameBtn = it.querySelector('div.lt_box > div > div.nick > button');
                        var linkBtn = it.querySelector('div.lt_box > a');
                        var profileImg = it.querySelector('div.lt_box > a > img');
                        var liveBadge = it.querySelector('div.lt_box > a > span.live');
                        var isLiveByBadge = liveBadge !== null;
                        var href = linkBtn ? linkBtn.getAttribute('href') : '';
                        var isLiveByUrl = href.includes('play.sooplive.co.kr');
                        var isLive = isLiveByBadge || isLiveByUrl;
                        var bjId = '';
                        if (href.includes('station/')) {
                            bjId = href.split('station/')[1].split('/')[0].split('?')[0];
                        } else if (href.includes('play.sooplive.co.kr/')) {
                            var match = href.match(/play\.sooplive\.co\.kr\/([^/]+)/);
                            if(match) bjId = match[1];
                        }
                        if(nameBtn && bjId) {
                            results.push({
                                name: nameBtn.innerText.trim(),
                                bjId: bjId,
                                profile: profileImg ? (profileImg.currentSrc || profileImg.src) : '',
                                isLive: isLive,
                                fanCount: it.querySelector('div.lt_box > div > div.fav > span.num')?.innerText || '0'
                            });
                        }
                    } catch (e) {}
                }
                return JSON.stringify(results);
            })()";

                    var raw = await SoopWebView.ExecuteScriptAsync(script);
                    if (!string.IsNullOrEmpty(raw) && raw != "null")
                    {
                        string? decoded = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(raw);
                        if (!string.IsNullOrEmpty(decoded))
                        {
                            var jArray = JArray.Parse(decoded);
                            foreach (var item in jArray)
                            {
                                var bjId = item["bjId"]?.ToString() ?? "";
                                var isLive = item["isLive"]?.Value<bool>() ?? false;
                                var prefix = bjId.Length >= 2 ? bjId.Substring(0, 2) : "";
                                var profileUrl = !string.IsNullOrEmpty(prefix)
                                    ? $"https://stimg.sooplive.co.kr/LOGO/{prefix}/{bjId}/m/{bjId}.webp"
                                    : "";

                                list.Add(new StreamerInfo
                                {
                                    Platform = "SOOP",
                                    StreamerId = bjId,
                                    StreamerName = item["name"]?.ToString() ?? "",
                                    ProfileImageUrl = profileUrl,
                                    FanCount = ParseFanCount(item["fanCount"]?.ToString() ?? "0"),
                                    IsLive = isLive,
                                    ChannelUrl = $"https://www.sooplive.co.kr/station/{bjId}"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("SOOP 채널 검색 실패", ex);
            }
            return list;
        }

        private void DisplaySearchResults(List<StreamerInfo> results)
        {
            foreach (var result in results)
            {
                result.IsFollowing = FollowManager.IsFollowing(result.Platform, result.StreamerId);

                // [쇼케이스 모드] 개인정보 마스킹
                if (_appSettings.ShowcaseMode)
                {
                    // THESALT 계정은 예외 처리 (그대로 표시)
                    if (!result.StreamerName.Equals("THESALT", StringComparison.OrdinalIgnoreCase))
                    {
                        result.StreamerName = GeneratePseudonym(result.StreamerName, result.Platform);
                        result.Description = "게임과 일상을 공유하는 스트리머입니다";
                        result.LoadedProfileImage = GetShowcasePlaceholderImage();
                    }
                }
            }

            var soopResults = results.Where(x => x.Platform == "SOOP").ToList();
            var chzzkResults = results.Where(x => x.Platform == "치지직").ToList();

            if (results.Count == 0)
            {
                SearchEmptyState.Visibility = Visibility.Visible;
                SearchResultsGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                SearchEmptyState.Visibility = Visibility.Collapsed;
                SearchResultsGrid.Visibility = Visibility.Visible;
                SoopResultsList.ItemsSource = soopResults;
                ChzzkResultsList.ItemsSource = chzzkResults;
                TxtSoopCount.Text = $"({soopResults.Count})";
                TxtChzzkCount.Text = $"({chzzkResults.Count})";

                // 프로필 이미지 로딩
                if (_appSettings.ShowcaseMode)
                {
                    // 쇼케이스 모드: THESALT 계정만 이미지 로드
                    _ = Task.Run(async () =>
                    {
                        var allStreamers = soopResults.Concat(chzzkResults);
                        var thesaltStreamers = allStreamers.Where(s => s.StreamerName.Equals("THESALT", StringComparison.OrdinalIgnoreCase));
                        var tasks = thesaltStreamers.Select(s => s.LoadProfileImageAsync());
                        await Task.WhenAll(tasks);
                    });
                }
                else
                {
                    // 일반 모드: 모든 이미지 로드
                    _ = Task.Run(async () =>
                    {
                        var allStreamers = soopResults.Concat(chzzkResults);
                        var tasks = allStreamers.Select(s => s.LoadProfileImageAsync());
                        await Task.WhenAll(tasks);
                    });
                }
            }
        }

        private void OnStreamerClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement el && el.DataContext is StreamerInfo info)
                {
                    LogInfo($"채널 열기: {info.StreamerName} ({info.Platform})");
                    Process.Start(new ProcessStartInfo(info.ChannelUrl) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                LogError("채널 열기 실패", ex);
                ShowErrorMessage("채널을 여는 중 오류가 발생했습니다.");
            }
        }
        #endregion

        #region 애니메이션 (기존 코드 유지)
        private void InitializeLoadingTimer()
        {
            if (_tipTimer != null) { _tipTimer.Stop(); _tipTimer = null; }
            _tipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.0) };
            _tipTimer.Tick += (s, e) =>
            {
                if (_loadingTips.Length == 0) return;
                _currentTipIndex = (_currentTipIndex + 1) % _loadingTips.Length;
                var fadeOut = new DoubleAnimation(0.7, 0, TimeSpan.FromMilliseconds(400));
                fadeOut.Completed += (s2, e2) =>
                {
                    if (TxtLoadingTip != null)
                    {
                        TxtLoadingTip.Text = _loadingTips[_currentTipIndex];
                        TxtLoadingTip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.7, TimeSpan.FromMilliseconds(400)));
                    }
                };
                TxtLoadingTip?.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
        }

        private void StartBackgroundGradientAnimation()
        {
            try
            {
                _backgroundGradientStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
                var brush = (LinearGradientBrush)this.FindResource("DynamicGradientBackground");
                var stops = brush.GradientStops;
                var colorAnim1 = CreateColorAnimation(Color.FromRgb(0, 40, 25), Color.FromRgb(10, 25, 50), Color.FromRgb(50, 10, 10));
                Storyboard.SetTarget(colorAnim1, stops[0]);
                Storyboard.SetTargetProperty(colorAnim1, new PropertyPath(GradientStop.ColorProperty));
                _backgroundGradientStoryboard.Children.Add(colorAnim1);
                var colorAnim2 = CreateColorAnimation(Color.FromRgb(10, 25, 50), Color.FromRgb(50, 10, 10), Color.FromRgb(0, 40, 25));
                Storyboard.SetTarget(colorAnim2, stops[1]);
                Storyboard.SetTargetProperty(colorAnim2, new PropertyPath(GradientStop.ColorProperty));
                _backgroundGradientStoryboard.Children.Add(colorAnim2);
                var colorAnim3 = CreateColorAnimation(Color.FromRgb(50, 10, 10), Color.FromRgb(0, 40, 25), Color.FromRgb(10, 25, 50));
                Storyboard.SetTarget(colorAnim3, stops[2]);
                Storyboard.SetTargetProperty(colorAnim3, new PropertyPath(GradientStop.ColorProperty));
                _backgroundGradientStoryboard.Children.Add(colorAnim3);
                _backgroundGradientStoryboard.Begin();
            }
            catch (Exception ex) { LogError("배경 애니메이션 시작 실패", ex); }
        }

        private ColorAnimationBase CreateColorAnimation(Color from, Color mid, Color to)
        {
            var anim = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(15) };
            anim.KeyFrames.Add(new LinearColorKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
            anim.KeyFrames.Add(new LinearColorKeyFrame(mid, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(7.5))));
            anim.KeyFrames.Add(new LinearColorKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(15))));
            return anim;
        }

        private void SetLoading(bool isLoading)
        {
            LoadingLayer.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            BtnRefresh.IsEnabled = !isLoading;
            if (isLoading)
            {
                InitializeLoadingTimer();
                if (TxtLoadingTip != null) TxtLoadingTip.Text = _loadingTips[0];
                _tipTimer?.Start();
                if (_loadingStoryboard == null) _loadingStoryboard = CreateOrbitAnimation();
                _loadingStoryboard?.Begin();
            }
            else
            {
                _tipTimer?.Stop();
                _loadingStoryboard?.Stop();
            }
        }

        private Storyboard CreateOrbitAnimation()
        {
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            double cx = 87.5, cy = 93.3;
            ConfigureOrbit(sb, Ball1, 0, cx, cy, 2.5);
            ConfigureOrbit(sb, Ball2, 120, cx, cy, 1.8);
            ConfigureOrbit(sb, Ball3, 240, cx, cy, 3.2);
            return sb;
        }

        private void ConfigureOrbit(Storyboard sb, Ellipse target, double startAngle, double cx, double cy, double maxScale)
        {
            double radius = 65;
            var animX = new DoubleAnimationUsingKeyFrames();
            var animY = new DoubleAnimationUsingKeyFrames();
            var animSX = CreateScaleAnim(target, "ScaleX");
            var animSY = CreateScaleAnim(target, "ScaleY");
            Storyboard.SetTarget(animX, target);
            Storyboard.SetTargetProperty(animX, new PropertyPath("RenderTransform.Children[1].X"));
            Storyboard.SetTarget(animY, target);
            Storyboard.SetTargetProperty(animY, new PropertyPath("RenderTransform.Children[1].Y"));
            double totalDuration = 2.0;
            for (int step = 0; step <= 40; step++)
            {
                double t = step / 40.0, timePos = t * totalDuration, currentAngle, currentRadius, currentScale = 1.0;
                if (t <= 0.25) { double p = t / 0.25; currentAngle = startAngle + (p * 180); currentRadius = radius * (1 - p); }
                else if (t <= 0.55) { double p = (t - 0.25) / 0.3; currentAngle = startAngle + 180; currentRadius = 0; currentScale = 1.0 + (Math.Sin(p * Math.PI) * (maxScale - 1.0)); }
                else if (t <= 0.8) { double p = (t - 0.55) / 0.25; currentAngle = startAngle + 180 + (p * 180); currentRadius = radius * p; }
                else { double p = (t - 0.8) / 0.2; currentAngle = startAngle + 360 + (p * 120); currentRadius = radius; }
                double rad = (currentAngle - 90) * Math.PI / 180;
                double tx = cx + Math.Cos(rad) * currentRadius, ty = cy + Math.Sin(rad) * currentRadius;
                var keyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(timePos));
                animX.KeyFrames.Add(new LinearDoubleKeyFrame(tx, keyTime));
                animY.KeyFrames.Add(new LinearDoubleKeyFrame(ty, keyTime));
                animSX.KeyFrames.Add(new LinearDoubleKeyFrame(currentScale, keyTime));
                animSY.KeyFrames.Add(new LinearDoubleKeyFrame(currentScale, keyTime));
            }
            sb.Children.Add(animX); sb.Children.Add(animY); sb.Children.Add(animSX); sb.Children.Add(animSY);
        }

        private DoubleAnimationUsingKeyFrames CreateScaleAnim(Ellipse target, string property)
        {
            var anim = new DoubleAnimationUsingKeyFrames();
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, new PropertyPath($"RenderTransform.Children[0].{property}"));
            return anim;
        }
        #endregion

        #region 데이터 로딩 (기존 코드 유지, 간략화)
        private async void RefreshData()
        {
            if (_isRefreshing) return;
            var timeSinceLastFetch = DateTime.Now - _lastFetchTime;
            if (_cachedData != null && timeSinceLastFetch.TotalSeconds < CACHE_DURATION_SECONDS)
            {
                LogInfo($"캐시된 데이터 사용");
                DisplayData(_cachedData);
                return;
            }
            _isRefreshing = true;
            SetLoading(true);
            TopThreeList.ItemsSource = null;
            NormalList.ItemsSource = null;
            _animationDelayCounter = 0;
            try
            {
                LogInfo("데이터 새로고침 시작");
                var chzzkTask = GetChzzkStreams();
                var soopTask = GetSoopStreams();
                var allTasks = Task.WhenAll(chzzkTask, soopTask);
                var completedTask = await Task.WhenAny(allTasks, Task.Delay(15000));

                List<StreamItem> chzzkData, soopData;
                if (completedTask == allTasks)
                {
                    chzzkData = chzzkTask.Result;
                    soopData = soopTask.Result;
                    LogInfo($"데이터 수집 완료 - 치지직: {chzzkData.Count}개, SOOP: {soopData.Count}개");
                }
                else
                {
                    LogError("15초 타임아웃");
                    chzzkData = chzzkTask.IsCompleted ? chzzkTask.Result : new List<StreamItem>();
                    soopData = soopTask.IsCompleted ? soopTask.Result : new List<StreamItem>();
                }
                var combined = chzzkData.Concat(soopData).OrderByDescending(x => x.ViewerCount).ToList();
                for (int i = 0; i < combined.Count; i++) combined[i].Rank = i + 1;
                _cachedData = combined;
                _lastFetchTime = DateTime.Now;
                DisplayData(combined);
            }
            catch (Exception ex)
            {
                LogError("데이터 새로고침 중 오류", ex);
                ShowErrorMessage("데이터를 불러오는 중 오류가 발생했습니다.");
            }
            finally { SetLoading(false); _isRefreshing = false; }
        }

        private void DisplayData(List<StreamItem> data)
        {
            // [쇼케이스 모드] 썸네일 숨김 처리
            if (_appSettings.ShowcaseMode)
            {
                // 방송 제목 랜덤 생성용 키워드
                string[] gameGenres = { "RPG", "FPS", "시뮬레이션", "전략", "MOBA", "배틀로얄", "어드벤처", "퍼즐" };
                string[] actions = { "플레이", "도전", "정복", "탐험", "생존", "대결", "클리어", "진행" };
                var rng = new Random(DateTime.Now.Millisecond);

                foreach (var item in data)
                {
                    // THESALT 계정은 예외 처리 (그대로 표시)
                    if (item.StreamerName.Equals("THESALT", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // 이 스트리머는 변경하지 않음
                    }

                    // 스트리머 이름 가명으로 변경
                    item.StreamerName = GeneratePseudonym(item.StreamerName, item.Platform);

                    // 방송 제목을 자연스러운 게임 방송 제목으로 변경
                    string genre = gameGenres[rng.Next(gameGenres.Length)];
                    string action = actions[rng.Next(actions.Length)];
                    item.Title = $"{genre} {action} 중 🎮";

                    item.ThumbnailUrl = "";
                    item.LoadedThumbnailImage = null;
                    item.LoadedProfileImage = GetShowcasePlaceholderImage();
                }
            }

            var top3 = data.Take(3).ToList();
            var normal = data.Skip(3).ToList();
            TopThreeList.ItemsSource = top3;
            NormalList.ItemsSource = normal;

            // [쇼케이스 모드] 이미지 로딩 건너뛰기
            if (!_appSettings.ShowcaseMode)
            {
                _ = Task.Run(async () =>
                {
                    var allItems = top3.Concat(normal);
                    var tasks = allItems.Select(item => item.LoadImagesAsync());
                    await Task.WhenAll(tasks);
                });
            }
        }

        private void ShowErrorMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                if (TxtLoadingTip != null)
                {
                    TxtLoadingTip.Text = message;
                    TxtLoadingTip.Foreground = new SolidColorBrush(Colors.OrangeRed);
                }
            });
        }

        private async void Item_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                int delay = _animationDelayCounter++ * 40;
                var tt = new TranslateTransform(0, 30);
                element.RenderTransform = tt;
                element.Opacity = 0;
                await Task.Delay(Math.Min(delay, 1200));
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500));
                var slide = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                element.BeginAnimation(UIElement.OpacityProperty, fade);
                tt.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

        private async Task<List<StreamItem>> GetChzzkStreams()
        {
            var list = new List<StreamItem>();
            const int MAX_RETRIES = 2;
            for (int retry = 0; retry <= MAX_RETRIES; retry++)
            {
                try
                {
                    var options = new RestClientOptions("https://api.chzzk.naver.com/service/v1/lives?size=24&sortType=POPULAR")
                    {
                        Timeout = TimeSpan.FromSeconds(10)
                    };
                    var client = new RestClient(options);
                    var response = await client.ExecuteAsync(new RestRequest());

                    if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                    {
                        var json = JObject.Parse(response.Content);
                        var data = json["content"]?["data"];
                        if (data != null)
                        {
                            foreach (var item in data)
                            {
                                list.Add(new StreamItem
                                {
                                    Platform = "치지직",
                                    Title = item["liveTitle"]?.ToString() ?? "",
                                    StreamerName = item["channel"]?["channelName"]?.ToString() ?? "",
                                    ViewerCount = item["concurrentUserCount"]?.Value<int>() ?? 0,
                                    ThumbnailUrl = item["liveImageUrl"]?.ToString().Replace("{type}", "1080") ?? "",
                                    ProfileImageUrl = item["channel"]?["channelImageUrl"]?.ToString() ?? "",
                                    LiveUrl = "https://chzzk.naver.com/live/" + item["channel"]?["channelId"]
                                });
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex) { LogError($"치지직 실패 (시도 {retry + 1})", ex); if (retry < MAX_RETRIES) await Task.Delay(1000); }
            }
            return list;
        }

        private async Task<List<StreamItem>> GetSoopStreams()
        {
            var list = new List<StreamItem>();
            if (SoopWebView?.CoreWebView2 == null)
            {
                LogError("SOOP WebView가 초기화되지 않음");
                return list;
            }

            const int MAX_RETRIES = 2;
            for (int retry = 0; retry <= MAX_RETRIES; retry++)
            {
                try
                {
                    LogInfo($"SOOP 데이터 요청 시작 (시도 {retry + 1})");
                    var tcs = new TaskCompletionSource<bool>();
                    EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;

                    handler = (s, e) =>
                    {
                        if (handler != null && SoopWebView?.CoreWebView2 != null)
                            SoopWebView.CoreWebView2.NavigationCompleted -= handler;
                        tcs.TrySetResult(e.IsSuccess);
                    };

                    SoopWebView.CoreWebView2.NavigationCompleted += handler;
                    SoopWebView.CoreWebView2.Navigate("https://www.sooplive.co.kr/live/all");

                    if (await Task.WhenAny(tcs.Task, Task.Delay(8000)) == tcs.Task && await tcs.Task)
                    {
                        LogInfo("SOOP 페이지 로드 성공");
                        for (int i = 0; i < 10; i++)
                        {
                            await Task.Delay(500);
                            var itemCount = await SoopWebView.ExecuteScriptAsync("document.querySelectorAll('#container > div.cBox-list > ul > li').length");
                            if (itemCount != null && itemCount != "0")
                            {
                                LogInfo($"SOOP 아이템 로딩 완료: {itemCount}개");
                                break;
                            }
                        }

                        string script = @"(function(){
                            var items = document.querySelectorAll('#container > div.cBox-list > ul > li');
                            var results = [];
                            for(var i=0; i<Math.min(items.length, 24); i++){
                                var it = items[i];
                                try {
                                    var t = it.querySelector('div.cBox-info h3 a');
                                    var n = it.querySelector('div.nick_wrap a span');
                                    var v = it.querySelector('span.views em');
                                    var th = it.querySelector('div.thumbs-box img');
                                    var link = t ? t.href : '';
                                    var segments = link.split('/');
                                    var bjId = (segments.length >= 2) ? segments[segments.length - 2] : '';
                                    
                                    if(t && n && bjId) {
                                        results.push({
                                            title: t.innerText, 
                                            streamer: n.innerText, 
                                            viewers: v ? v.innerText : '0',
                                            thumb: th ? (th.getAttribute('data-src') || th.src) : '',
                                            bjId: bjId,
                                            link: link
                                        });
                                    }
                                } catch(e){}
                            }
                            return JSON.stringify(results);
                        })()";

                        var raw = await SoopWebView.ExecuteScriptAsync(script);
                        if (!string.IsNullOrEmpty(raw) && raw != "null")
                        {
                            string? decoded = Newtonsoft.Json.JsonConvert.DeserializeObject<string>(raw);
                            if (!string.IsNullOrEmpty(decoded))
                            {
                                var jArray = JArray.Parse(decoded);
                                LogInfo($"SOOP 파싱 완료: {jArray.Count}개");

                                foreach (var item in jArray)
                                {
                                    var bjId = item["bjId"]?.ToString() ?? "";
                                    string profileUrl = "";

                                    if (!string.IsNullOrEmpty(bjId) && bjId.Length >= 2)
                                    {
                                        var prefix = bjId.Substring(0, 2);
                                        profileUrl = $"https://stimg.sooplive.co.kr/LOGO/{prefix}/{bjId}/m/{bjId}.webp";
                                    }

                                    list.Add(new StreamItem
                                    {
                                        Platform = "SOOP",
                                        Title = item["title"]?.ToString() ?? "",
                                        StreamerName = item["streamer"]?.ToString() ?? "",
                                        ViewerCount = ParseViewerCount(item["viewers"]?.ToString() ?? "0"),
                                        ThumbnailUrl = item["thumb"]?.ToString() ?? "",
                                        ProfileImageUrl = profileUrl,
                                        LiveUrl = item["link"]?.ToString() ?? ""
                                    });
                                }
                                LogInfo($"SOOP 데이터 {list.Count}개 수집 완료");
                                break;
                            }
                        }
                    }
                    else
                    {
                        LogError("SOOP 페이지 로딩 타임아웃");
                    }
                }
                catch (Exception ex)
                {
                    LogError($"SOOP 데이터 수집 실패 (시도 {retry + 1})", ex);
                    if (retry < MAX_RETRIES) await Task.Delay(1000);
                }
            }

            return list;
        }

        private int ParseViewerCount(string v)
        {
            try
            {
                v = v.Replace(",", "").Replace("명", "").Trim();
                if (v.Contains("만") && double.TryParse(v.Replace("만", ""), out double val)) return (int)(val * 10000);
                int.TryParse(v, out int res);
                return res;
            }
            catch { return 0; }
        }

        private int ParseFanCount(string v)
        {
            try
            {
                v = v.Replace(",", "").Trim();
                if (v.Contains("만") && double.TryParse(v.Replace("만", ""), out double val)) return (int)(val * 10000);
                int.TryParse(v, out int res);
                return res;
            }
            catch { return 0; }
        }
        #endregion

        #region 팔로우 기능
        private void InitializeFollowSystem()
        {
            try
            {
                var loadedFollows = FollowManager.LoadFollows();
                LogInfo($"팔로우 데이터 로드 완료: {loadedFollows.Count}명");

                // [수정] SearchSoopChannels 메서드를 전달
                _liveChecker = new FollowLiveChecker(SoopWebView, Dispatcher, SearchSoopChannels);
                _liveChecker.LiveStatusChanged += OnLiveStatusChanged;

                // [핵심 추가] 모든 팔로우 체크 완료 이벤트 구독
                _liveChecker.AllFollowChecksCompleted += OnAllFollowChecksCompleted;

                // [추가] 저장된 체크 주기 설정 불러와서 적용
                int savedInterval = LoadCheckInterval();
                _liveChecker.CheckIntervalMinutes = savedInterval;
                LogInfo($"체크 주기 설정 적용: {savedInterval}분");

                _liveChecker.Start();

                // [변경] 즉시 체크는 MainWindow_Loaded에서 메인화면 로딩 후 실행되도록 변경
                // Task.Run으로 즉시 체크하는 코드 제거

                LogInfo("팔로우 시스템 초기화 완료");
            }
            catch (Exception ex)
            {
                LogError("팔로우 시스템 초기화 실패", ex);
            }
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _trayIconManager = new TrayIconManager(this);
                _trayIconManager.OnShowWindow += () =>
                {
                    // 트레이 모드 해제
                    _isInTrayMode = false;
                    this.ShowInTaskbar = true;
                    LogInfo("트레이 모드 해제됨");
                };
                LogInfo("트레이 아이콘 초기화 완료");
            }
            catch (Exception ex)
            {
                LogError("트레이 아이콘 초기화 실패", ex);
            }
        }

        /// <summary>
        /// Windows 알림 클릭 시 호출되는 핸들러
        /// </summary>
        private void OnToastNotificationActivated(ToastNotificationActivatedEventArgsCompat e)
        {
            try
            {
                LogInfo($"[알림 클릭] 알림이 클릭되었습니다. Arguments: {e.Argument}");

                // 알림 버튼의 Arguments 파싱
                ToastArguments args = ToastArguments.Parse(e.Argument);

                // "시청하기" 버튼 클릭 시
                if (args.Contains("action") && args["action"] == "viewStream")
                {
                    string? url = args.Contains("url") ? args["url"] : null;

                    if (!string.IsNullOrEmpty(url))
                    {
                        LogInfo($"[알림 클릭] 방송 URL 열기: {url}");

                        // UI 스레드에서 실행
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                // 브라우저로 URL 열기
                                Process.Start(new ProcessStartInfo(url)
                                {
                                    UseShellExecute = true
                                });

                                LogInfo($"[알림 클릭] 방송 열기 성공: {url}");
                            }
                            catch (Exception ex)
                            {
                                LogError($"[알림 클릭] 방송 열기 실패", ex);
                            }
                        });
                    }
                    else
                    {
                        LogError("[알림 클릭] URL이 비어있습니다");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("[알림 클릭] 알림 클릭 처리 실패", ex);
            }
        }

        private void OnLiveStatusChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (FollowPage.Visibility == Visibility.Visible)
                {
                    // 기존 ItemsSource를 다시 가져와서 정렬만 다시 적용
                    var follows = FollowManager.GetAllFollows(); // 내부에서 정렬됨

                    // 치지직과 SOOP으로 분리
                    var chzzkFollows = follows.Where(f => f.Platform == "치지직").ToList();
                    var soopFollows = follows.Where(f => f.Platform == "SOOP").ToList();

                    ChzzkFollowList.ItemsSource = null;
                    ChzzkFollowList.ItemsSource = chzzkFollows;

                    SoopFollowList.ItemsSource = null;
                    SoopFollowList.ItemsSource = soopFollows;

                    // 카운트 업데이트
                    FollowCountText.Text = $"팔로우 {follows.Count}명";

                    // 빈 메시지 처리
                    if (follows.Count == 0)
                    {
                        ChzzkFollowList.Visibility = Visibility.Collapsed;
                        SoopFollowList.Visibility = Visibility.Collapsed;
                        EmptyFollowMessage.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        EmptyFollowMessage.Visibility = Visibility.Collapsed;
                        ChzzkFollowList.Visibility = chzzkFollows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                        SoopFollowList.Visibility = soopFollows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            });
        }

        private void OnAllFollowChecksCompleted(object? sender, EventArgs e)
        {
            try
            {
                // [변경] 메인 페이지 로딩은 MainWindow_Loaded에서 이미 시작되므로 여기서는 제거
                LogInfo("모든 팔로우 체크 완료");

                // 팔로우 페이지가 열려있다면 업데이트
                if (!_isInTrayMode)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (FollowPage.Visibility == Visibility.Visible)
                        {
                            LoadFollowPage();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LogError("팔로우 체크 완료 핸들러 오류", ex);
            }
        }

        private async void LoadFollowPage()
        {
            try
            {
                var follows = FollowManager.GetAllFollows();
                FollowCountText.Text = $"팔로우 {follows.Count}명";

                if (follows.Count == 0)
                {
                    ChzzkFollowList.Visibility = Visibility.Collapsed;
                    SoopFollowList.Visibility = Visibility.Collapsed;
                    EmptyFollowMessage.Visibility = Visibility.Visible;
                }
                else
                {
                    EmptyFollowMessage.Visibility = Visibility.Collapsed;

                    // 플랫폼별로 분리
                    var chzzkFollows = follows.Where(f => f.Platform == "치지직").ToList();
                    var soopFollows = follows.Where(f => f.Platform == "SOOP").ToList();

                    // [쇼케이스 모드] 개인정보 마스킹
                    if (_appSettings.ShowcaseMode)
                    {
                        foreach (var follow in follows)
                        {
                            // THESALT 계정은 예외 처리 (그대로 표시)
                            if (follow.StreamerName.Equals("THESALT", StringComparison.OrdinalIgnoreCase))
                            {
                                // 일반 모드처럼 프로필 이미지 로드
                                if (follow.LoadedProfileImage == null)
                                {
                                    follow.LoadedProfileImage = await WebPImageLoader.LoadImageAsync(follow.ProfileImageUrl);
                                }
                            }
                            else
                            {
                                follow.StreamerName = GeneratePseudonym(follow.StreamerName, follow.Platform);
                                follow.LoadedProfileImage = GetShowcasePlaceholderImage();
                            }
                        }
                    }
                    else
                    {
                        // 프로필 이미지 로드 (일반 모드만)
                        foreach (var follow in follows)
                        {
                            if (follow.LoadedProfileImage == null)
                            {
                                follow.LoadedProfileImage = await WebPImageLoader.LoadImageAsync(follow.ProfileImageUrl);
                            }
                        }
                    }

                    // 각 리스트에 바인딩
                    ChzzkFollowList.ItemsSource = chzzkFollows;
                    SoopFollowList.ItemsSource = soopFollows;

                    // 빈 플랫폼 숨기기
                    ChzzkFollowList.Visibility = chzzkFollows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    SoopFollowList.Visibility = soopFollows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                    // [제거] 실시간 체크 제거 - 백그라운드 체커에만 의존
                    // 팔로우 탭에서 별도로 체크하면 백그라운드 타이머와 충돌 발생
                    // _ = Task.Run(async () => await UpdateFollowLiveStatus(follows));
                }
            }
            catch (Exception ex)
            {
                LogError("팔로우 페이지 로드 실패", ex);
            }
        }

        private void BtnFollowStreamer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.DataContext is StreamerInfo streamer)
                {
                    if (streamer.IsFollowing)
                    {
                        if (FollowManager.RemoveFollow(streamer.Platform, streamer.StreamerId))
                        {
                            streamer.IsFollowing = false;
                            LogInfo($"언팔로우: {streamer.StreamerName}");
                        }
                    }
                    else
                    {
                        var followedStreamer = new FollowedStreamer
                        {
                            Platform = streamer.Platform,
                            StreamerId = streamer.StreamerId,
                            StreamerName = streamer.StreamerName,
                            ProfileImageUrl = streamer.ProfileImageUrl,
                            ChannelUrl = streamer.ChannelUrl,
                            IsLive = streamer.IsLive,
                            FollowedAt = DateTime.Now
                        };

                        if (FollowManager.AddFollow(followedStreamer))
                        {
                            streamer.IsFollowing = true;
                            LogInfo($"팔로우: {streamer.StreamerName}");
                            Task.Run(async () => await _liveChecker?.CheckAllStreamersAsync()!);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("팔로우 처리 실패", ex);
            }
        }

        private void BtnUnfollowStreamer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.DataContext is FollowedStreamer streamer)
                {
                    if (FollowManager.RemoveFollow(streamer.Platform, streamer.StreamerId))
                    {
                        LogInfo($"언팔로우: {streamer.StreamerName}");
                        LoadFollowPage();
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("언팔로우 처리 실패", ex);
            }
        }

        private async void BtnRefreshFollow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_liveChecker == null)
                {
                    LogError("팔로우 체커가 초기화되지 않음");
                    return;
                }

                LogInfo("수동 팔로우 새로고침 시작");

                // 버튼 비활성화
                if (sender is Button btn)
                {
                    btn.IsEnabled = false;
                    btn.Content = "⏳";
                }

                // 백그라운드 체커를 이용해 즉시 체크
                await _liveChecker.CheckAllStreamersAsync();

                LogInfo("수동 팔로우 새로고침 완료");

                // 버튼 다시 활성화
                if (sender is Button btn2)
                {
                    btn2.IsEnabled = true;
                    btn2.Content = "🔄";
                }
            }
            catch (Exception ex)
            {
                LogError("팔로우 새로고침 실패", ex);

                // 오류 발생 시에도 버튼 복구
                if (sender is Button btn)
                {
                    btn.IsEnabled = true;
                    btn.Content = "🔄";
                }
            }
        }

        private void OnFollowedStreamerClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement el && el.DataContext is FollowedStreamer streamer)
                {
                    LogInfo($"방송 열기: {streamer.StreamerName}");
                    Process.Start(new ProcessStartInfo(streamer.ChannelUrl) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                LogError("방송 열기 실패", ex);
            }
        }
        #endregion

        #region 설정 기능
        private void LoadSettings()
        {
            try
            {
                // [추가] 로딩 중 플래그 설정
                _isLoadingSettings = true;

                // [변경] 이미 메모리에 로드된 설정 사용 (파일 다시 읽지 않음)
                ChkAutoStart.IsChecked = _appSettings.AutoStart;
                ChkMinimizeToTray.IsChecked = _appSettings.MinimizeToTray;
                ChkShowcaseMode.IsChecked = _appSettings.ShowcaseMode;
                SliderCheckInterval.Value = _appSettings.CheckIntervalMinutes;
                UpdateCheckIntervalDisplay(_appSettings.CheckIntervalMinutes);

                // FollowLiveChecker에 설정 적용
                if (_liveChecker != null)
                {
                    _liveChecker.CheckIntervalMinutes = _appSettings.CheckIntervalMinutes;
                }

                // [추가] 레지스트리의 자동 시작 설정과 동기화
                bool isAutoStartInRegistry = IsAutoStartEnabled();
                if (_appSettings.AutoStart && !isAutoStartInRegistry)
                {
                    EnableAutoStart(true);
                }
                else if (!_appSettings.AutoStart && isAutoStartInRegistry)
                {
                    DisableAutoStart();
                }

                LogInfo($"[LoadSettings] 설정 페이지 표시 - AutoStart: {_appSettings.AutoStart}, MinimizeToTray: {_appSettings.MinimizeToTray}, ShowcaseMode: {_appSettings.ShowcaseMode}, CheckInterval: {_appSettings.CheckIntervalMinutes}분");
            }
            catch (Exception ex)
            {
                LogError("설정 로드 실패", ex);
            }
            finally
            {
                // [추가] 로딩 완료 후 플래그 해제
                _isLoadingSettings = false;
            }
        }

        private void SliderCheckInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (_isLoadingSettings)
                {
                    LogInfo($"[SliderCheckInterval] 로딩 중이므로 저장 건너뜀 (값: {(int)e.NewValue}분)");
                    return;
                }

                int interval = (int)e.NewValue;
                UpdateCheckIntervalDisplay(interval);

                _appSettings.CheckIntervalMinutes = interval;
                _appSettings.Save();

                if (_liveChecker != null)
                {
                    _liveChecker.CheckIntervalMinutes = interval;
                }

                LogInfo($"체크 주기 변경: {interval}분");
            }
            catch (Exception ex)
            {
                LogError("체크 주기 변경 실패", ex);
            }
        }

        private void UpdateCheckIntervalDisplay(int minutes)
        {
            if (TxtCheckInterval != null)
            {
                TxtCheckInterval.Text = $"{minutes}분";
            }

            if (TxtCheckIntervalDescription != null)
            {
                TxtCheckIntervalDescription.Text = $"팔로우한 스트리머의 방송 상태를 {minutes}분마다 확인합니다";
            }
        }

        private int LoadCheckInterval()
        {
            // [변경] AppSettings에서 로드
            return _appSettings.CheckIntervalMinutes;
        }

        private void SaveCheckInterval(int minutes)
        {
            // [변경] AppSettings에 저장
            _appSettings.CheckIntervalMinutes = minutes;
            _appSettings.Save();
        }

        private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings)
                {
                    LogInfo($"[ChkAutoStart] 로딩 중이므로 저장 건너뜀 (값: {ChkAutoStart.IsChecked})");
                    return;
                }

                bool isChecked = ChkAutoStart.IsChecked == true;

                _appSettings.AutoStart = isChecked;
                _appSettings.Save();

                if (isChecked)
                {
                    EnableAutoStart(true);
                }
                else
                {
                    DisableAutoStart();
                }

                LogInfo($"자동 시작 설정 변경: {isChecked}");
            }
            catch (Exception ex)
            {
                LogError("자동 시작 설정 변경 실패", ex);
            }
        }

        private void ChkMinimizeToTray_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings)
                {
                    LogInfo($"[ChkMinimizeToTray] 로딩 중이므로 저장 건너뜀 (값: {ChkMinimizeToTray.IsChecked})");
                    return;
                }

                _appSettings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
                _appSettings.Save();
                LogInfo($"트레이로 최소화 설정 변경: {_appSettings.MinimizeToTray}");
            }
            catch (Exception ex)
            {
                LogError("트레이로 최소화 설정 변경 실패", ex);
            }
        }

        private void ChkShowcaseMode_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isLoadingSettings)
                {
                    LogInfo($"[ChkShowcaseMode] 로딩 중이므로 저장 건너뜀 (값: {ChkShowcaseMode.IsChecked})");
                    return;
                }

                _appSettings.ShowcaseMode = ChkShowcaseMode.IsChecked == true;
                _appSettings.Save();
                LogInfo($"쇼케이스 모드 설정 변경: {_appSettings.ShowcaseMode}");

                // 즉시 현재 페이지 새로고침
                if (MainPage.Visibility == Visibility.Visible)
                {
                    RefreshData();
                }
                else if (FollowPage.Visibility == Visibility.Visible)
                {
                    LoadFollowPage();
                }
            }
            catch (Exception ex)
            {
                LogError("쇼케이스 모드 설정 변경 실패", ex);
            }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("Flax") != null;
            }
            catch
            {
                return false;
            }
        }

        private void EnableAutoStart(bool backgroundMode = false)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    // 백그라운드 모드 옵션 추가
                    var startupCommand = backgroundMode ? $"\"{exePath}\" --background" : $"\"{exePath}\"";

                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    key?.SetValue("Flax", startupCommand);
                    LogInfo($"자동 시작 활성화됨 (백그라운드: {backgroundMode})");
                }
            }
            catch (Exception ex)
            {
                LogError("자동 시작 활성화 실패", ex);
            }
        }

        private void DisableAutoStart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("Flax", false);
                LogInfo("자동 시작 비활성화됨");
            }
            catch (Exception ex)
            {
                LogError("자동 시작 비활성화 실패", ex);
            }
        }
        #endregion

        #region 윈도우 컨트롤
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState == WindowState.Minimized)
            {
                // [변경] AppSettings에서 읽어오기
                if (_isInTrayMode || _appSettings.MinimizeToTray)
                {
                    Hide();
                    _trayIconManager?.ShowBalloonTip("Flax", "백그라운드에서 실행 중입니다.");
                }
            }
        }

        /// <summary>
        /// 전체화면 토글
        /// </summary>
        private void Fullscreen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isFullscreen)
                {
                    // 전체화면으로 전환
                    _previousWindowStyle = this.WindowStyle;
                    _previousWidth = this.Width;
                    _previousHeight = this.Height;
                    _previousLeft = this.Left;
                    _previousTop = this.Top;
                    _previousWindowState = this.WindowState;

                    this.WindowStyle = WindowStyle.None;
                    this.WindowState = WindowState.Maximized;
                    _isFullscreen = true;

                    BtnFullscreen.Content = "⛶"; // 아이콘 변경
                    LogInfo("전체화면 모드로 전환");
                }
                else
                {
                    // 원래 크기로 복원
                    this.WindowStyle = _previousWindowStyle;
                    this.WindowState = _previousWindowState;
                    this.Width = _previousWidth;
                    this.Height = _previousHeight;
                    this.Left = _previousLeft;
                    this.Top = _previousTop;
                    _isFullscreen = false;

                    BtnFullscreen.Content = "⛶";
                    LogInfo("전체화면 모드 해제");
                }
            }
            catch (Exception ex)
            {
                LogError("전체화면 전환 실패", ex);
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            ExitConfirmPopup.Visibility = Visibility.Visible;
        }

        private void BtnExitCancel_Click(object sender, RoutedEventArgs e)
        {
            ExitConfirmPopup.Visibility = Visibility.Collapsed;
        }

        private void BtnExitConfirm_Click(object sender, RoutedEventArgs e)
        {
            // [변경] AppSettings에서 읽어오기
            bool minimizeToTray = _appSettings.MinimizeToTray;

            if (minimizeToTray)
            {
                // 트레이 모드로 전환
                _isInTrayMode = true;
                this.WindowState = WindowState.Minimized;
                this.ShowInTaskbar = false;
                ExitConfirmPopup.Visibility = Visibility.Collapsed;
                Hide();
                _trayIconManager?.ShowBalloonTip("Flax", "백그라운드에서 팔로우 상태를 계속 추적합니다.");
                LogInfo("트레이 모드로 전환됨");
            }
            else
            {
                // 완전 종료
                _liveChecker?.Stop();
                _trayIconManager?.Dispose();

                // [추가] 알림 이벤트 핸들러 해제
                ToastNotificationManagerCompat.OnActivated -= OnToastNotificationActivated;
                ToastNotificationManagerCompat.Uninstall();

                Application.Current.Shutdown();
                LogInfo("앱 완전 종료");
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _cachedData = null;
            _lastFetchTime = DateTime.MinValue;
            RefreshData();
        }

        private void OnStreamClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement el && el.DataContext is StreamItem item)
                {
                    LogInfo($"방송 열기: {item.StreamerName}");
                    Process.Start(new ProcessStartInfo(item.LiveUrl) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { LogError("방송 열기 실패", ex); }
        }
        #endregion
    }

    #region 데이터 모델
    public class StreamItem : INotifyPropertyChanged
    {
        private BitmapImage? _loadedProfileImage;
        private BitmapImage? _loadedThumbnailImage;
        public int Rank { get; set; }
        public string Platform { get; set; } = "";
        public string Title { get; set; } = "";
        public string StreamerName { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public string ProfileImageUrl { get; set; } = "";
        public string LiveUrl { get; set; } = "";
        public int ViewerCount { get; set; }
        public BitmapImage? LoadedProfileImage { get => _loadedProfileImage; set { _loadedProfileImage = value; OnPropertyChanged(); } }
        public BitmapImage? LoadedThumbnailImage { get => _loadedThumbnailImage; set { _loadedThumbnailImage = value; OnPropertyChanged(); } }
        public string RankDisplay => $"{Rank}위";
        public Brush RankBrush => Rank switch
        {
            1 => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700")),
            2 => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C0C0C0")),
            3 => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CD7F32")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252525"))
        };
        public string PlatformDisplay => $"[{Platform}]";
        public string PlatformColor => Platform == "치지직" ? "#00FFA3" : "#3787FF";
        public string ViewerDisplay => ViewerCount >= 10000 ? $"{(ViewerCount / 10000.0):F1}만" : $"{ViewerCount:N0}";
        public async Task LoadImagesAsync()
        {
            var profileTask = WebPImageLoader.LoadImageAsync(ProfileImageUrl);
            var thumbnailTask = WebPImageLoader.LoadImageAsync(ThumbnailUrl);
            LoadedProfileImage = await profileTask;
            LoadedThumbnailImage = await thumbnailTask;
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class StreamerInfo : INotifyPropertyChanged
    {
        private BitmapImage? _loadedProfileImage;
        private bool _isFollowing;
        public string Platform { get; set; } = "";
        public string StreamerName { get; set; } = "";
        public string StreamerId { get; set; } = "";
        public string ProfileImageUrl { get; set; } = "";
        public string Description { get; set; } = "";
        public int FollowerCount { get; set; }
        public int FanCount { get; set; }
        public bool IsLive { get; set; }
        public string ChannelUrl { get; set; } = "";
        public BitmapImage? LoadedProfileImage { get => _loadedProfileImage; set { _loadedProfileImage = value; OnPropertyChanged(); } }
        public bool IsFollowing
        {
            get => _isFollowing;
            set
            {
                _isFollowing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FollowButtonText));
                OnPropertyChanged(nameof(FollowButtonBackground));
            }
        }
        public string FollowButtonText => IsFollowing ? "팔로잉" : "팔로우";
        public Brush FollowButtonBackground => IsFollowing
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff6262"));
        public string PlatformDisplay => $"[{Platform}]";
        public string PlatformColor => Platform == "치지직" ? "#00FFA3" : "#3787FF";
        public string FollowerDisplay => FollowerCount > 0 ? (FollowerCount >= 10000 ? $"팔로워 {(FollowerCount / 10000.0):F1}만" : $"팔로워 {FollowerCount:N0}") : "";
        public string FanDisplay => FanCount > 0 ? (FanCount >= 10000 ? $"애청자 {(FanCount / 10000.0):F1}만" : $"애청자 {FanCount:N0}") : "";
        public string LiveStatusText => IsLive ? "LIVE" : "오프라인";
        public Brush LiveStatusBrush => IsLive ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555"));
        public async Task LoadProfileImageAsync() => LoadedProfileImage = await WebPImageLoader.LoadImageAsync(ProfileImageUrl);
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    #endregion
}