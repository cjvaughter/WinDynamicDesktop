using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static WinDynamicDesktop.WallpaperChangeScheduler;

namespace WinDynamicDesktop.WPF
{
    public class ThemePreviewItem
    {
        public string PreviewText { get; set; }
        public MemoryStream Data { get; set; }

        public ThemePreviewItem(string previewText, string path)
        {
            PreviewText = previewText;
            Data = new MemoryStream();
            using (var file = File.OpenRead(path))
            {
                file.CopyTo(Data);
            }
        }
    }

    public class ThemePreviewerViewModel : INotifyPropertyChanged
    {
        #region Properties

        private bool controlsVisible;
        public bool ControlsVisible
        {
            get => controlsVisible;
            set => SetProperty(ref controlsVisible, value);
        }

        private bool messageVisible;
        public bool MessageVisible
        {
            get => messageVisible;
            set => SetProperty(ref messageVisible, value);
        }

        private bool carouselVisible;
        public bool CarouselVisible
        {
            get => carouselVisible;
            set => SetProperty(ref carouselVisible, value);
        }

        private bool sliderVisible;
        public bool SliderVisible
        {
            get => sliderVisible;
            set => SetProperty(ref sliderVisible, value);
        }

        private string title;
        public string Title
        {
            get => title;
            set => SetProperty(ref title, value);
        }

        private string author;
        public string Author
        {
            get => author;
            set => SetProperty(ref author, value);
        }

        private string previewText;
        public string PreviewText
        {
            get => previewText;
            set => SetProperty(ref previewText, value);
        }

        private string message;
        public string Message
        {
            get => message;
            set => SetProperty(ref message, value);
        }

        private BitmapImage backImage;
        public BitmapImage BackImage
        {
            get => backImage;
            set => SetProperty(ref backImage, value);
        }

        private BitmapImage frontImage;
        public BitmapImage FrontImage
        {
            get => frontImage;
            set => SetProperty(ref frontImage, value);
        }

        private BitmapImage preloadImage;
        public BitmapImage PreloadImage
        {
            get => preloadImage;
            set => SetProperty(ref preloadImage, value);
        }

        private bool isPlaying;
        public bool IsPlaying
        {
            get => isPlaying;
            set => SetProperty(ref isPlaying, value);
        }

        private bool isMouseOver;
        public bool IsMouseOver
        {
            get => isMouseOver;
            set
            {
                SetProperty(ref isMouseOver, value);
                if (value)
                {
                    Pause();
                }
                else if (IsPlaying && fadeQueue.IsEmpty)
                {
                    Play();
                }
            }
        }

        private int selectedIndex;
        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                if (value != selectedIndex)
                {
                    GoTo(value);
                }
                SetProperty(ref selectedIndex, value);
            }
        }

        public ObservableCollection<ThemePreviewItem> Items { get; } = new ObservableCollection<ThemePreviewItem>();

        public bool EnableInterpolation { get; set; }

        private int sliderValue;
        public int SliderValue
        {
            get => sliderValue;
            set
            {
                SetProperty(ref sliderValue, value);
                time = today + TimeSpan.FromMinutes(SliderValue);
                OnPropertyChanged(nameof(Time));
                UpdateInterpolation();
            }
        }

        private DateTime time;
        public DateTime Time
        {
            get => time;
            set
            {
                SetProperty(ref time, value);
                sliderValue = (int)time.TimeOfDay.TotalMinutes;
                OnPropertyChanged(nameof(SliderValue));
            }
        }

        private double frontOpacity;
        public double FrontOpacity
        {
            get => frontOpacity;
            set => SetProperty(ref frontOpacity, value);
        }

        private string segmentText;
        public string SegmentText
        {
            get => segmentText;
            set => SetProperty(ref segmentText, value);
        }

        #endregion

        #region Commands

        public ICommand PlayCommand => new RelayCommand(() =>
        {
            IsPlaying = !IsPlaying;
            if (IsPlaying && !IsMouseOver && fadeQueue.IsEmpty)
            {
                Play();
            }
        });

        public ICommand PreviousCommand => new RelayCommand(Previous);

        public ICommand NextCommand => new RelayCommand(Next);

        #endregion

        private static readonly Func<string, string> _L = Localization.GetTranslation;

        private const int CAROUSEL_TIME = 5;
        private const int SLIDER_TIME = 60;

        private readonly DispatcherTimer carouselTimer;
        private readonly DispatcherTimer interpolationTimer;
        private readonly ConcurrentQueue<int> fadeQueue = new ConcurrentQueue<int>();
        private readonly SemaphoreSlim fadeSemaphore = new SemaphoreSlim(1, 1);
        private readonly Action startAnimation;
        private readonly Action stopAnimation;
        private readonly Action<double> setSpeed;
        private readonly int maxWidth;
        private readonly int maxHeight;

        private Dictionary<int, ThemePreviewItem> itemDict = new Dictionary<int, ThemePreviewItem>();
        private DateTime today = DateTime.Today;
        private int backImageId = -1;
        private int frontImageId = -1;
        private int preloadImageId = -1;
        private SolarData solarData;
        private ThemeConfig theme;

        public ThemePreviewerViewModel(Action startAnimation, Action stopAnimation, Action<double> setSpeed)
        {
            this.startAnimation = startAnimation;
            this.stopAnimation = stopAnimation;
            this.setSpeed = setSpeed;

            carouselTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(CAROUSEL_TIME)
            };
            carouselTimer.Tick += (s, e) => Next();

            interpolationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.0 / (1440.0 / SLIDER_TIME))
            };
            interpolationTimer.Tick += (s, e) =>
            {
                if (SliderValue + 1 >= 1440)
                {
                    SliderValue = 0;
                }
                else
                {
                    SliderValue++;
                }
            };

            IsPlaying = true;

            int maxArea = 0;
            foreach (Screen screen in Screen.AllScreens)
            {
                int area = screen.Bounds.Width * screen.Bounds.Height;
                if (area > maxArea)
                {
                    maxArea = area;
                    maxWidth = screen.Bounds.Width;
                    maxHeight = screen.Bounds.Height;
                }
            }
        }

        private static object preloadLock = new object();

        private async void UpdateInterpolation()
        {
            SchedulerState state = GetImageData(solarData, theme, Time);

            long total = state.endTick - state.startTick;
            long current = Time.Ticks - state.startTick;
            double percent = Interpolation.Calculate((double)current / total, theme.interpolation);

            if (state.imageId != backImageId)
            {
                backImageId = state.imageId;
                if (backImageId == frontImageId)
                {
                    BackImage = FrontImage;
                }
                else if (backImageId == preloadImageId)
                {
                    lock (preloadLock)
                    {
                        BackImage = PreloadImage;
                    }
                }
                else
                {
                    BackImage = CreateImage(itemDict[backImageId].Data);
                }
            }

            DateTime nextTime = new DateTime(state.endTick) + TimeSpan.FromSeconds(1);
            state = GetImageData(solarData, theme, nextTime);

            if (state.imageId != frontImageId)
            {
                frontImageId = state.imageId;
                if (frontImageId == backImageId)
                {
                    FrontImage = BackImage;
                }
                else if (frontImageId == preloadImageId)
                {
                    lock (preloadLock)
                    {
                        FrontImage = PreloadImage;
                    }
                }
                else
                {
                    FrontImage = CreateImage(itemDict[frontImageId].Data);
                }
            }

            string segment;
            switch (state.daySegment4)
            {
                case 0:
                    segment = _L("Sunrise");
                    break;
                case 1:
                    segment = _L("Day");
                    break;
                case 2:
                    segment = _L("Sunset");
                    break;
                default:
                case 3:
                    segment = _L("Night");
                    break;
            }
            PreviewText = $"{Time:hh:mm tt} ({segment})";
            SegmentText = $"{percent:P2}";
            FrontOpacity = percent;

            nextTime = new DateTime(state.endTick) + TimeSpan.FromSeconds(1);
            state = GetImageData(solarData, theme, nextTime);
            if (state.imageId != preloadImageId)
            {
                preloadImageId = state.imageId;
                Thread thread = new Thread(PreloadThread);
                thread.Start();
            }
        }

        private void PreloadThread()
        {
            lock (preloadLock)
            {
                PreloadImage = CreateImage(itemDict[preloadImageId].Data);
            }
        }

        private SchedulerState GetImageData(SolarData data, ThemeConfig theme, DateTime current)
        {
            if (current.Day > today.Day)
            {
                current = current.AddDays(today.Day - current.Day);
            }
            return AppContext.wpEngine.GetImageData(data, theme, current);
        }

        public void OnAnimationComplete()
        {   
            BackImage = FrontImage;
            FrontImage = null;

            if (fadeQueue.TryDequeue(out int index))
            {
                FrontImage = CreateImage(Items[index].Data);
                startAnimation();
            }
            else
            {
                TryRelease(fadeSemaphore);
                setSpeed(1);

                if (IsPlaying && !IsMouseOver)
                {
                    Play();
                }
            }
        }

        public void PreviewTheme(ThemeConfig theme)
        {
            Stop();

            int activeImage = 0;
            string[] sunrise = null;
            string[] day = null;
            string[] sunset = null;
            string[] night = null;

            today = DateTime.Today;
            solarData = SunriseSunsetService.GetSolarData(today);
            this.theme = theme;

            SchedulerState wpState = AppContext.wpEngine.GetImageData(solarData, theme, DateTime.Now);

            if (theme != null)
            {
                Title = ThemeManager.GetThemeName(theme);
                Author = ThemeManager.GetThemeAuthor(theme);
                ControlsVisible = true;

                if (ThemeManager.IsThemeDownloaded(theme))
                {
                    if (!theme.sunriseImageList.SequenceEqual(theme.dayImageList))
                    {
                        sunrise = ImagePaths(theme, theme.sunriseImageList);
                    }

                    day = ImagePaths(theme, theme.dayImageList);

                    if (!theme.sunsetImageList.SequenceEqual(theme.dayImageList))
                    {
                        sunset = ImagePaths(theme, theme.sunsetImageList);
                    }

                    night = ImagePaths(theme, theme.nightImageList);

                    EnableInterpolation = JsonConfig.settings.enableInterpolation && theme.interpolation != InterpolationMethod.None;

                    CarouselVisible = !EnableInterpolation;
                    SliderVisible = EnableInterpolation;
                }
                else
                {
                    Message = _L("Theme is not downloaded. Click Download button to enable full preview.");
                    MessageVisible = true;
                    CarouselVisible = true;

                    string path = Path.Combine("assets", "images", theme.themeId + "_{0}.jpg");

                    string file = string.Format(path, "sunrise");
                    if (File.Exists(file))
                    {
                        sunrise = new[] { file };
                    }

                    file = string.Format(path, "day");
                    if (File.Exists(file))
                    {
                        day = new[] { file };
                    }

                    file = string.Format(path, "sunset");
                    if (File.Exists(file))
                    {
                        sunset = new[] { file };
                    }

                    file = string.Format(path, "night");
                    if (File.Exists(file))
                    {
                        night = new[] { file };
                    }
                }

                AddItems(string.Format(_L("Previewing {0}"), _L("Sunrise")), sunrise, theme.sunriseImageList);
                AddItems(string.Format(_L("Previewing {0}"), _L("Day")), day, theme.dayImageList);
                AddItems(string.Format(_L("Previewing {0}"), _L("Sunset")), sunset, theme.sunsetImageList);
                AddItems(string.Format(_L("Previewing {0}"), _L("Night")), night, theme.nightImageList);

                if (wpState.daySegment4 >= 1)
                {
                    activeImage += sunrise?.Length ?? 0;
                }
                if (wpState.daySegment4 >= 2)
                {
                    activeImage += day?.Length ?? 0;
                }
                if (wpState.daySegment4 == 3)
                {
                    activeImage += sunset?.Length ?? 0;
                }

                activeImage += wpState.imageNumber;
            }
            else
            {
                Title = _L("None");
                Author = "Microsoft";

                Items.Add(new ThemePreviewItem(string.Empty, ThemeThumbLoader.GetWindowsWallpaper()));
            }

            Start(activeImage);
        }

        private void Previous()
        {
            if (SelectedIndex == 0)
            {
                SelectedIndex = Items.Count - 1;
            }
            else
            {
                SelectedIndex--;
            }
        }

        private void Next()
        {
            if (SelectedIndex == Items.Count - 1)
            {
                SelectedIndex = 0;
            }
            else
            {
                SelectedIndex++;
            }
        }

        private void Play()
        {
            if (EnableInterpolation)
            {
                interpolationTimer.Start();
            }
            else
            {
                carouselTimer.Start();
            }
        }
        
        private void Pause()
        {
            interpolationTimer.Stop();
            carouselTimer.Stop();
        }

        private void GoTo(int index)
        {
            if (index < 0 || index >= Items.Count) return;

            Pause();

            if (fadeSemaphore.Wait(0))
            {
                FrontImage = CreateImage(Items[index].Data);
                startAnimation();
            }
            else
            {
                fadeQueue.Enqueue(index);
                setSpeed(1 + fadeQueue.Count);
            }

            PreviewText = Items[index].PreviewText;
        }

        public void Stop()
        {
            stopAnimation();
            while (fadeQueue.TryDequeue(out _)) ;
            TryRelease(fadeSemaphore);
            setSpeed(1);

            Pause();

            today = DateTime.Today;
            backImageId = -1;
            frontImageId = -1;
            preloadImageId = -1;
            PreloadImage = null;

            ControlsVisible = false;
            MessageVisible = false;
            CarouselVisible = false;
            SliderVisible = false;
            Title = null;
            Author = null;
            PreviewText = null;
            Message = null;
            BackImage = null;
            FrontImage = null;
            SelectedIndex = -1;
            EnableInterpolation = false;
            sliderValue = 0;
            time = DateTime.Now;
            FrontOpacity = 0;
            SegmentText = null;

            foreach (var v in Items)
            {
                v.Data.Dispose();
            }

            Items.Clear();
            itemDict.Clear();
        }

        private void AddItems(string preview, string[] items, int[] ids)
        {
            if (items == null) return;

            for (int i = 0; i < items.Length; i++)
            {
                var item = new ThemePreviewItem($"{preview} ({i + 1}/{items.Length})", items[i]);
                Items.Add(item);

                if (ids != null)
                {
                    if (!itemDict.ContainsKey(ids[i]))
                    {
                        itemDict.Add(ids[i], item);
                    }
                }
            }
        }

        private void Start(int index)
        {
            var item = Items[index];

            PreviewText = item.PreviewText;

            if (EnableInterpolation)
            {
                Time = DateTime.Now;
                UpdateInterpolation();
            }
            else
            {
                BackImage = CreateImage(item.Data);
            }

            selectedIndex = index;
            OnPropertyChanged(nameof(SelectedIndex));

            if (IsPlaying && !IsMouseOver)
            {
                if (EnableInterpolation)
                {
                    Time = DateTime.Now;
                    interpolationTimer.Start();
                }
                else
                {
                    carouselTimer.Start();
                }
            }
        }

        private BitmapImage CreateImage(MemoryStream memory)
        {
            memory.Position = 0;

            BitmapImage img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.None;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.StreamSource = memory;

            if (maxWidth >= maxHeight)
            {
                img.DecodePixelWidth = maxWidth;
            }
            else
            {
                img.DecodePixelHeight = maxHeight;
            }

            img.EndInit();
            img.Freeze();
            return img;
        }

        private static string[] ImagePaths(ThemeConfig theme, int[] imageList)
        {
            return imageList.Select(id =>
                Path.Combine("themes", theme.themeId, theme.imageFilename.Replace("*", id.ToString()))).ToArray();
        }

        private static void TryRelease(SemaphoreSlim semaphore)
        {
            try
            {
                semaphore.Release();
            }
            catch (SemaphoreFullException) { }
        }


        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        private void SetProperty<T>(ref T field, T value, [CallerMemberName]string propertyName = "")
        {
            //if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                OnPropertyChanged(propertyName);
            }
        }

        private void OnPropertyChanged([CallerMemberName]string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
