using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace Flax
{
    /// <summary>
    /// 팔로우한 스트리머 정보
    /// </summary>
    public class FollowedStreamer : INotifyPropertyChanged
    {
        private BitmapImage? _loadedProfileImage;
        private bool _isCheckingLiveStatus = false;

        public string Platform { get; set; } = "";
        public string StreamerId { get; set; } = ""; // 치지직: channelId, SOOP: bjId
        public string StreamerName { get; set; } = "";
        public string ProfileImageUrl { get; set; } = "";
        public string ChannelUrl { get; set; } = "";
        public bool IsLive { get; set; } = false;
        public DateTime LastChecked { get; set; } = DateTime.MinValue;
        public DateTime FollowedAt { get; set; } = DateTime.Now;

        // [중요 수정] 이미지는 JSON 파일에 저장하지 않음 (로드할 때 다시 다운로드/캐시사용 하므로)
        [JsonIgnore]
        public BitmapImage? LoadedProfileImage
        {
            get => _loadedProfileImage;
            set
            {
                _loadedProfileImage = value;
                OnPropertyChanged();
            }
        }

        // 라이브 상태 체크 중인지 표시
        [JsonIgnore]
        public bool IsCheckingLiveStatus
        {
            get => _isCheckingLiveStatus;
            set
            {
                _isCheckingLiveStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LiveStatusText));
                OnPropertyChanged(nameof(LiveStatusBrush));
            }
        }

        // [중요 수정] UI용 텍스트/색상은 저장하지 않음 (실시간 데이터로 계산됨)
        [JsonIgnore]
        public string LiveStatusText => IsCheckingLiveStatus ? "확인중..." : (IsLive ? "LIVE" : "오프라인");

        [JsonIgnore]
        public Brush LiveStatusBrush => IsCheckingLiveStatus
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA500"))
            : (IsLive
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 팔로우 목록 관리
    /// </summary>
    public static class FollowManager
    {
        private static readonly string FOLLOW_FILE_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Flax",
            "follows.json"
        );

        private static List<FollowedStreamer> _followedStreamers = new List<FollowedStreamer>();

        /// <summary>
        /// 팔로우 목록 로드
        /// </summary>
        public static List<FollowedStreamer> LoadFollows()
        {
            try
            {
                // 파일 경로 디렉토리가 없으면 생성 (안전장치)
                var directory = Path.GetDirectoryName(FOLLOW_FILE_PATH);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory!);

                if (File.Exists(FOLLOW_FILE_PATH))
                {
                    var json = File.ReadAllText(FOLLOW_FILE_PATH);
                    // 빈 파일이거나 내용이 없을 경우 처리
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _followedStreamers = new List<FollowedStreamer>();
                    }
                    else
                    {
                        _followedStreamers = JsonConvert.DeserializeObject<List<FollowedStreamer>>(json) ?? new List<FollowedStreamer>();

                        // [수정] 앱 실행 시 모든 스트리머의 IsLive를 false로 초기화
                        // 프로그램이 꺼져있는 동안 방송이 시작/종료되었을 수 있으므로
                        // 초기화 후 실제 체크를 통해 현재 상태를 정확히 파악
                        foreach (var streamer in _followedStreamers)
                        {
                            streamer.IsLive = false;
                        }
                        System.Diagnostics.Debug.WriteLine($"[FollowManager] 팔로우 목록 로드 완료 - 모든 IsLive 초기화됨 ({_followedStreamers.Count}명)");
                    }
                }
                else
                {
                    _followedStreamers = new List<FollowedStreamer>();
                }
            }
            catch (Exception ex)
            {
                // JSON 형식이 깨져있을 경우 백업 후 초기화
                System.Diagnostics.Debug.WriteLine($"팔로우 목록 로드 실패: {ex.Message}");

                // 기존 파일이 깨져서 못 읽는 경우일 수 있으므로 백업해두고 초기화
                if (File.Exists(FOLLOW_FILE_PATH))
                {
                    try { File.Move(FOLLOW_FILE_PATH, FOLLOW_FILE_PATH + ".bak", true); } catch { }
                }

                _followedStreamers = new List<FollowedStreamer>();
            }
            return _followedStreamers;
        }

        private static readonly object _fileLock = new object();

        /// <summary>
        /// 팔로우 목록 저장 (파일 락 보호)
        /// </summary>
        public static void SaveFollows()
        {
            lock (_fileLock)
            {
                int maxRetries = 3;
                int retryDelayMs = 100;

                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(FOLLOW_FILE_PATH);
                        if (!Directory.Exists(directory))
                            Directory.CreateDirectory(directory!);

                        var json = JsonConvert.SerializeObject(_followedStreamers, Formatting.Indented);
                        File.WriteAllText(FOLLOW_FILE_PATH, json);
                        return; // 성공 시 즉시 리턴
                    }
                    catch (IOException ex) when (attempt < maxRetries - 1)
                    {
                        // 파일이 다른 프로세스에서 사용 중일 때 재시도
                        System.Diagnostics.Debug.WriteLine($"팔로우 목록 저장 재시도 ({attempt + 1}/{maxRetries}): {ex.Message}");
                        System.Threading.Thread.Sleep(retryDelayMs);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"팔로우 목록 저장 실패: {ex.Message}");
                        return;
                    }
                }
                System.Diagnostics.Debug.WriteLine($"팔로우 목록 저장 실패: 최대 재시도 횟수 초과");
            }
        }

        public static bool AddFollow(FollowedStreamer streamer)
        {
            if (_followedStreamers.Any(f => f.Platform == streamer.Platform && f.StreamerId == streamer.StreamerId))
                return false;

            _followedStreamers.Add(streamer);
            SaveFollows();
            return true;
        }

        public static bool RemoveFollow(string platform, string streamerId)
        {
            var removed = _followedStreamers.RemoveAll(f => f.Platform == platform && f.StreamerId == streamerId);
            if (removed > 0)
            {
                SaveFollows();
                return true;
            }
            return false;
        }

        public static bool IsFollowing(string platform, string streamerId)
        {
            return _followedStreamers.Any(f => f.Platform == platform && f.StreamerId == streamerId);
        }

        /// <summary>
        /// 라이브 상태 업데이트 및 알림 필요 여부 반환
        /// [핵심 개선] HasNotifiedLive 제거하고 이전 상태와 비교하는 방식으로 변경
        /// </summary>
        /// <returns>오프라인→라이브 전환 시 true (알림 필요), 그 외 false</returns>
        public static bool UpdateLiveStatus(string platform, string streamerId, bool isLive)
        {
            var streamer = _followedStreamers.FirstOrDefault(f => f.Platform == platform && f.StreamerId == streamerId);
            if (streamer == null)
                return false;

            // 이전 상태 저장
            bool wasOffline = !streamer.IsLive;

            // 현재 상태 업데이트
            streamer.IsLive = isLive;
            streamer.LastChecked = DateTime.Now;

            SaveFollows();

            // 오프라인 → 라이브 전환 시 알림 필요
            bool shouldNotify = wasOffline && isLive;

            if (shouldNotify)
            {
                System.Diagnostics.Debug.WriteLine($"[FollowManager] {streamer.StreamerName} 라이브 시작 감지 (오프라인→라이브)");
            }

            return shouldNotify;
        }

        public static List<FollowedStreamer> GetAllFollows()
        {
            // 원본 리스트를 정렬하여 반환 (객체 참조 유지를 위해 ToList() 제거)
            // 정렬은 in-place로 수행
            _followedStreamers.Sort((a, b) =>
            {
                // 먼저 IsLive 기준으로 내림차순 (LIVE가 먼저)
                int liveCompare = b.IsLive.CompareTo(a.IsLive);
                if (liveCompare != 0) return liveCompare;

                // 그 다음 이름 기준으로 오름차순
                return string.Compare(a.StreamerName, b.StreamerName, StringComparison.Ordinal);
            });

            return _followedStreamers;
        }

        public static int GetFollowCount()
        {
            return _followedStreamers.Count;
        }
    }
}