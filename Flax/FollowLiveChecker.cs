using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace Flax
{
    /// <summary>
    /// 팔로우한 스트리머의 라이브 상태를 주기적으로 체크
    /// </summary>
    public class FollowLiveChecker
    {
        private readonly DispatcherTimer _checkTimer;
        private int _checkIntervalMinutes = 5; // 기본값 5분
        private bool _isChecking = false;
        private readonly WebView2? _webView;
        private readonly Dispatcher? _dispatcher;

        // [추가] SearchSoopChannels 메서드 참조를 위한 델리게이트
        private readonly Func<string, Task<List<StreamerInfo>>>? _searchSoopChannelsFunc;

        public event EventHandler? LiveStatusChanged;

        // [추가] 모든 팔로우 체크 완료 이벤트 (치지직 + SOOP 모두)
        public event EventHandler? AllFollowChecksCompleted;

        /// <summary>
        /// 체크 주기 (분 단위)
        /// </summary>
        public int CheckIntervalMinutes
        {
            get => _checkIntervalMinutes;
            set
            {
                if (value < 1) value = 1; // 최소 1분
                if (value > 60) value = 60; // 최대 60분
                _checkIntervalMinutes = value;
                _checkTimer.Interval = TimeSpan.FromMinutes(_checkIntervalMinutes);
                System.Diagnostics.Debug.WriteLine($"[FollowChecker] 체크 주기 변경: {_checkIntervalMinutes}분");
            }
        }

        public FollowLiveChecker(WebView2? webView = null, Dispatcher? dispatcher = null, Func<string, Task<List<StreamerInfo>>>? searchSoopChannels = null)
        {
            _webView = webView;
            _dispatcher = dispatcher;
            _searchSoopChannelsFunc = searchSoopChannels;

            _checkTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(_checkIntervalMinutes)
            };
            _checkTimer.Tick += async (s, e) => await CheckAllStreamersAsync();
        }

        /// <summary>
        /// 라이브 체크 시작 (타이머만 시작, 즉시 체크는 별도 호출)
        /// </summary>
        public void Start()
        {
            _checkTimer.Start();
            System.Diagnostics.Debug.WriteLine("[FollowChecker] 타이머 시작됨");
        }

        /// <summary>
        /// 라이브 체크 중지
        /// </summary>
        public void Stop()
        {
            _checkTimer.Stop();
        }

        /// <summary>
        /// 즉시 모든 스트리머 체크 (백그라운드 시작시 호출)
        /// </summary>
        public async Task CheckImmediatelyAsync()
        {
            System.Diagnostics.Debug.WriteLine("[FollowChecker] 즉시 체크 시작");
            await CheckAllStreamersAsync();

            // [추가] 모든 체크 완료 이벤트 발생
            System.Diagnostics.Debug.WriteLine("[FollowChecker] 모든 팔로우 체크 완료 - 이벤트 발생");
            AllFollowChecksCompleted?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 모든 팔로우 스트리머 체크
        /// </summary>
        public async Task CheckAllStreamersAsync()
        {
            if (_isChecking)
            {
                System.Diagnostics.Debug.WriteLine("[FollowChecker] 이미 체크 진행 중 - 건너뜀");
                return;
            }

            _isChecking = true;

            try
            {
                var follows = FollowManager.GetAllFollows();
                if (follows.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[FollowChecker] 팔로우 목록이 비어있음");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[FollowChecker] ===== {follows.Count}명의 스트리머 라이브 상태 체크 시작 =====");

                // UI에 체크 중 상태 표시
                if (_dispatcher != null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        foreach (var f in follows)
                        {
                            f.IsCheckingLiveStatus = true;
                        }
                    });
                }

                // 플랫폼별로 그룹화
                var chzzkFollows = follows.Where(f => f.Platform == "치지직").ToList();
                var soopFollows = follows.Where(f => f.Platform == "SOOP").ToList();

                System.Diagnostics.Debug.WriteLine($"[FollowChecker] 치지직: {chzzkFollows.Count}명, SOOP: {soopFollows.Count}명");

                // 병렬로 체크
                var chzzkTask = CheckChzzkStreamersAsync(chzzkFollows);
                var soopTask = CheckSoopStreamersAsync(soopFollows);

                await Task.WhenAll(chzzkTask, soopTask);

                System.Diagnostics.Debug.WriteLine($"[FollowChecker] ===== 라이브 상태 체크 완료 =====");

                // UI 체크 중 상태 해제 및 라이브 상태 변경 이벤트 발생
                if (_dispatcher != null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        foreach (var f in follows)
                        {
                            f.IsCheckingLiveStatus = false;
                        }
                        // 라이브 상태 변경 이벤트 발생
                        LiveStatusChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FollowChecker] 체크 중 오류: {ex.Message}");

                // 오류 발생 시에도 체크 중 상태 해제
                if (_dispatcher != null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        var follows = FollowManager.GetAllFollows();
                        foreach (var f in follows)
                        {
                            f.IsCheckingLiveStatus = false;
                        }
                    });
                }
            }
            finally
            {
                _isChecking = false;
            }
        }

        /// <summary>
        /// 치지직 스트리머 라이브 체크 (검색과 동일한 방식)
        /// </summary>
        private async Task CheckChzzkStreamersAsync(List<FollowedStreamer> streamers)
        {
            if (streamers.Count == 0) return;

            var client = new RestClient();
            foreach (var streamer in streamers)
            {
                try
                {
                    // 1. 스트리머 이름으로 검색 (검색 창 로직과 동일)
                    var url = $"https://api.chzzk.naver.com/service/v1/search/channels?keyword={Uri.EscapeDataString(streamer.StreamerName)}&size=1";
                    var request = new RestRequest(url, Method.Get);
                    var response = await client.ExecuteAsync(request);

                    if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                    {
                        var json = JObject.Parse(response.Content);
                        var channelNode = json["content"]?["data"]?.FirstOrDefault();

                        if (channelNode != null)
                        {
                            // 2. 검색된 채널의 ID가 일치하는지 확인
                            var foundChannelId = channelNode["channel"]?["channelId"]?.ToString();
                            if (foundChannelId == streamer.StreamerId)
                            {
                                // 3. 핵심: 치지직 API의 필드명은 'openLive' 입니다!
                                bool isNowLive = channelNode["channel"]?["openLive"]?.Value<bool>() ?? false;

                                System.Diagnostics.Debug.WriteLine($"[치지직 체크] {streamer.StreamerName}: {(isNowLive ? "방송 중" : "방종")} (이전: {(streamer.IsLive ? "방송 중" : "방종")})");

                                // 4. [핵심 수정] UpdateLiveStatus가 알림 필요 여부를 반환함
                                bool shouldNotify = FollowManager.UpdateLiveStatus(streamer.Platform, streamer.StreamerId, isNowLive);

                                System.Diagnostics.Debug.WriteLine($"[치지직 체크] shouldNotify = {shouldNotify}");

                                // 5. 알림 필요 시 전송
                                if (shouldNotify)
                                {
                                    System.Diagnostics.Debug.WriteLine($"========================================");
                                    System.Diagnostics.Debug.WriteLine($"[치지직 알림] {streamer.StreamerName} 라이브 시작 알림 전송 시작!");
                                    System.Diagnostics.Debug.WriteLine($"========================================");
                                    SendLiveNotification(streamer);
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[치지직] {streamer.StreamerName} - 알림 불필요 (상태 변화 없음)");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FollowChecker] {streamer.StreamerName} 체크 중 오류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// SOOP 스트리머 라이브 체크 (팔로우 탭과 동일한 방식 - SearchSoopChannels 사용)
        /// </summary>
        private async Task CheckSoopStreamersAsync(List<FollowedStreamer> streamers)
        {
            if (streamers.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[FollowChecker] SOOP: 체크할 스트리머 없음");
                return;
            }

            // SearchSoopChannels 함수가 없으면 체크 불가
            if (_searchSoopChannelsFunc == null)
            {
                System.Diagnostics.Debug.WriteLine("[FollowChecker] SOOP: SearchSoopChannels 함수가 제공되지 않음");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[FollowChecker] SOOP: {streamers.Count}명 체크 시작");

            try
            {
                foreach (var streamer in streamers)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[FollowChecker] SOOP {streamer.StreamerName} 체크 중...");

                        // [핵심] 팔로우 탭과 동일하게 SearchSoopChannels 호출
                        List<StreamerInfo>? searchResults = null;

                        if (_dispatcher != null)
                        {
                            searchResults = await await _dispatcher.InvokeAsync(async () =>
                            {
                                return await _searchSoopChannelsFunc(streamer.StreamerName);
                            });
                        }
                        else
                        {
                            searchResults = await _searchSoopChannelsFunc(streamer.StreamerName);
                        }

                        if (searchResults != null && searchResults.Count > 0)
                        {
                            // StreamerId가 일치하는 스트리머 찾기
                            var target = searchResults.FirstOrDefault(s =>
                                s.StreamerId.Equals(streamer.StreamerId, StringComparison.OrdinalIgnoreCase));

                            if (target != null)
                            {
                                bool isNowLive = target.IsLive;

                                System.Diagnostics.Debug.WriteLine($"[SOOP 체크] {streamer.StreamerName}: {(isNowLive ? "LIVE" : "OFFLINE")} (이전: {(streamer.IsLive ? "LIVE" : "OFFLINE")})");

                                // [핵심 수정] UpdateLiveStatus가 알림 필요 여부를 반환함
                                bool shouldNotify = FollowManager.UpdateLiveStatus(streamer.Platform, streamer.StreamerId, isNowLive);

                                System.Diagnostics.Debug.WriteLine($"[SOOP 체크] shouldNotify = {shouldNotify}");

                                // 알림 필요 시 전송
                                if (shouldNotify)
                                {
                                    System.Diagnostics.Debug.WriteLine($"========================================");
                                    System.Diagnostics.Debug.WriteLine($"[SOOP 알림] {streamer.StreamerName} 라이브 시작 알림 전송 시작!");
                                    System.Diagnostics.Debug.WriteLine($"========================================");
                                    SendLiveNotification(streamer);
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[SOOP] {streamer.StreamerName} - 알림 불필요 (상태 변화 없음)");
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[FollowChecker] SOOP {streamer.StreamerName} - 검색 결과에서 일치하는 StreamerId를 찾지 못함");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[FollowChecker] SOOP {streamer.StreamerName} - 검색 결과 없음");
                        }

                        await Task.Delay(500); // 다음 체크 전 대기
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[FollowChecker] SOOP {streamer.StreamerName} 체크 실패: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FollowChecker] SOOP 전체 체크 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// Windows 알림 전송
        /// </summary>
        private void SendLiveNotification(FollowedStreamer streamer)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"========================================");
                System.Diagnostics.Debug.WriteLine($"[알림] SendLiveNotification 호출됨!");
                System.Diagnostics.Debug.WriteLine($"[알림] 스트리머: {streamer.StreamerName}");
                System.Diagnostics.Debug.WriteLine($"[알림] 플랫폼: {streamer.Platform}");
                System.Diagnostics.Debug.WriteLine($"[알림] URL: {streamer.ChannelUrl}");
                System.Diagnostics.Debug.WriteLine($"========================================");

                new ToastContentBuilder()
                    .AddText($"{streamer.StreamerName}님이 방송 중 입니다!!")
                    .AddText($"{streamer.Platform}")
                    .AddButton(new ToastButton()
                        .SetContent("시청하기")
                        .AddArgument("action", "viewStream")
                        .AddArgument("platform", streamer.Platform)
                        .AddArgument("streamerId", streamer.StreamerId)
                        .AddArgument("url", streamer.ChannelUrl))
                    .Show();

                System.Diagnostics.Debug.WriteLine($"[알림 전송 완료] {streamer.StreamerName} ({streamer.Platform}) - URL: {streamer.ChannelUrl}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[알림 ERROR] 알림 전송 실패: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[알림 ERROR] 스택: {ex.StackTrace}");
            }
        }
    }
}