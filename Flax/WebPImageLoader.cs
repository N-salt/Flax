using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Flax
{
    /// <summary>
    /// WebP 이미지를 비동기로 로드하고 캐싱하는 헬퍼 클래스
    /// UI 블로킹 없이 백그라운드에서 변환 처리
    /// </summary>
    public static class WebPImageLoader
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)  // 10초 타임아웃 추가
        };
        private static readonly ConcurrentDictionary<string, BitmapImage> _cache = new ConcurrentDictionary<string, BitmapImage>();
        private static readonly BitmapImage _placeholderImage = CreatePlaceholderImage();

        /// <summary>
        /// URL에서 이미지를 비동기로 로드 (캐시 우선)
        /// </summary>
        public static async Task<BitmapImage> LoadImageAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
                return _placeholderImage;

            // 캐시 확인
            if (_cache.TryGetValue(url, out var cachedImage))
                return cachedImage;

            try
            {
                // 백그라운드 스레드에서 다운로드 및 변환
                var imageBytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);

                // ImageSharp로 디코딩
                using (var image = SixLabors.ImageSharp.Image.Load(imageBytes))
                {
                    // PNG로 변환 (메모리 스트림)
                    using (var memoryStream = new MemoryStream())
                    {
                        await image.SaveAsPngAsync(memoryStream).ConfigureAwait(false);
                        memoryStream.Position = 0;

                        // UI 스레드에서 BitmapImage 생성
                        var bitmapImage = await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = memoryStream;
                            bitmap.EndInit();
                            bitmap.Freeze(); // 스레드 간 공유 가능하게
                            return bitmap;
                        });

                        // 캐시 저장
                        _cache.TryAdd(url, bitmapImage);
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"이미지 로드 실패: {url}, 오류: {ex.Message}");
                return _placeholderImage;
            }
        }

        /// <summary>
        /// 투명한 1x1 플레이스홀더 이미지 생성
        /// </summary>
        private static BitmapImage CreatePlaceholderImage()
        {
            byte[] transparentPixel =
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
                0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
                0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
                0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
            };

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(transparentPixel);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 캐시 클리어 (메모리 절약용)
        /// </summary>
        public static void ClearCache()
        {
            _cache.Clear();
        }
    }
}