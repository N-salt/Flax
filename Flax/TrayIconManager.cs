using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace Flax
{
    /// <summary>
    /// 시스템 트레이 아이콘 관리 (Hardcodet.NotifyIcon.Wpf 사용)
    /// 백그라운드에서 앱을 실행하고 알림을 받을 수 있도록 함
    /// </summary>
    public class TrayIconManager : IDisposable
    {
        private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _taskbarIcon;
        private readonly Window _mainWindow;

        /// <summary>
        /// 창 표시 이벤트
        /// </summary>
        public event Action? OnShowWindow;

        public TrayIconManager(Window mainWindow)
        {
            _mainWindow = mainWindow;
            InitializeTrayIcon();
        }

        /// <summary>
        /// 트레이 아이콘 초기화
        /// </summary>
        private void InitializeTrayIcon()
        {
            try
            {
                _taskbarIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
                {
                    ToolTipText = "Flax - 통합 방송 시청 플랫폼",
                    Visibility = Visibility.Visible
                };

                // 아이콘 리소스 설정 (App.xaml에 정의된 아이콘 사용 또는 기본 아이콘)
                _taskbarIcon.IconSource = new BitmapImage(new Uri("pack://application:,,,/Flax.ico"));

                // 컨텍스트 메뉴 생성
                var contextMenu = new ContextMenu();

                var showMenuItem = new MenuItem { Header = "열기" };
                showMenuItem.Click += (s, e) => ShowWindow();

                var separator = new Separator();

                var exitMenuItem = new MenuItem { Header = "종료" };
                exitMenuItem.Click += (s, e) => ExitApplication();

                contextMenu.Items.Add(showMenuItem);
                contextMenu.Items.Add(separator);
                contextMenu.Items.Add(exitMenuItem);

                _taskbarIcon.ContextMenu = contextMenu;

                // 더블클릭으로 창 열기
                _taskbarIcon.TrayMouseDoubleClick += (s, e) => ShowWindow();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"트레이 아이콘 초기화 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 창 표시
        /// </summary>
        private void ShowWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();

            // 이벤트 발생
            OnShowWindow?.Invoke();
        }

        /// <summary>
        /// 앱 종료
        /// </summary>
        private void ExitApplication()
        {
            _taskbarIcon?.Dispose();
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 알림 풍선 표시
        /// </summary>
        public void ShowBalloonTip(string title, string message)
        {
            try
            {
                _taskbarIcon?.ShowBalloonTip(title, message, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"트레이 알림 표시 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 리소스 정리
        /// </summary>
        public void Dispose()
        {
            if (_taskbarIcon != null)
            {
                _taskbarIcon.Visibility = Visibility.Collapsed;
                _taskbarIcon.Dispose();
                _taskbarIcon = null;
            }
        }
    }
}