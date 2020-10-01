// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.IO;
using System.Timers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

namespace WinDynamicDesktop
{
    public class SchedulerState
    {
        public int imageId;
        public int imageNumber;
        public long startTick;
        public long endTick;
        public int daySegment2;
        public int daySegment4;
    }

    public class InterpolationState
    {
        public int imageId1;
        public int imageId2;
        public long startTick;
        public long endTick;
        public double lastPercent;
    }

    class WallpaperChangeScheduler
    {
        private enum DaySegment { Sunrise, Day, Sunset, Night, AllDay, AllNight };

        private string lastImagePath;
        private DateTime? nextUpdateTime;

        public FullScreenApi fullScreenChecker;

        private Timer backgroundTimer = new Timer();
        private Timer schedulerTimer = new Timer();
        private const long timerError = (long)(TimeSpan.TicksPerMillisecond * 15.6);

        private InterpolationState interpolation = new InterpolationState();
        private static readonly object interpolationLock = new object();

        public WallpaperChangeScheduler()
        {
            fullScreenChecker = new FullScreenApi(this);

            backgroundTimer.AutoReset = true;
            backgroundTimer.Interval = 60e3;
            backgroundTimer.Elapsed += OnBackgroundTimerElapsed;
            backgroundTimer.Start();

            schedulerTimer.Elapsed += OnSchedulerTimerElapsed;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.TimeChanged += OnTimeChanged;
        }

        public static bool IsSunUp(SolarData data, DateTime current)
        {
            return (data.sunriseTime <= current && current < data.sunsetTime);
        }

        public void RunScheduler(bool forceImageUpdate = false)
        {
            if (!LaunchSequence.IsLocationReady() || !LaunchSequence.IsThemeReady())
            {
                return;
            }

            schedulerTimer.Stop();

            SolarData data = SunriseSunsetService.GetSolarData(DateTime.Today);
            bool isSunUp = IsSunUp(data, DateTime.Now);
            DateTime? nextImageUpdateTime = null;

            if (ThemeManager.currentTheme != null)
            {
                if (forceImageUpdate)
                {
                    lastImagePath = null;
                }

                WallpaperShuffler.MaybeShuffleWallpaper();
            }

            SchedulerState imageData = GetImageData(data, ThemeManager.currentTheme, DateTime.Now);

            if (ThemeManager.currentTheme != null)
            {
                nextImageUpdateTime = new DateTime(imageData.endTick);

                if (JsonConfig.settings.enableInterpolation)
                {
                    SchedulerState nextImageData = GetImageData(data, ThemeManager.currentTheme, nextImageUpdateTime.Value + TimeSpan.FromSeconds(1));

                    lock (interpolationLock)
                    {
                        interpolation.imageId1 = imageData.imageId;
                        interpolation.imageId2 = nextImageData.imageId;
                        interpolation.lastPercent = -1;
                        interpolation.startTick = imageData.startTick;
                        interpolation.endTick = imageData.endTick;
                        lastImagePath = null;
                        UpdateInterpolation();
                    }
                }
                else
                {
                    SetWallpaper(imageData.imageId);
                }
            }

            ScriptManager.RunScripts(new ScriptArgs
            {
                daySegment2 = imageData.daySegment2,
                daySegment4 = imageData.daySegment4,
                imagePath = (ThemeManager.currentTheme != null) ? lastImagePath : null
            });

            if (data.polarPeriod != PolarPeriod.None)
            {
                nextUpdateTime = DateTime.Today.AddDays(1);
            }
            else if (isSunUp)
            {
                nextUpdateTime = data.sunsetTime;
            }
            else if (DateTime.Now < data.solarTimes[0])
            {
                nextUpdateTime = data.sunriseTime;
            }
            else
            {
                SolarData tomorrowsData = SunriseSunsetService.GetSolarData(DateTime.Today.AddDays(1));
                nextUpdateTime = tomorrowsData.sunriseTime;
            }

            if (nextImageUpdateTime.HasValue && nextImageUpdateTime.Value < nextUpdateTime.Value)
            {
                nextUpdateTime = nextImageUpdateTime;
            }

            StartTimer(nextUpdateTime.Value);
        }

        public void ToggleDarkMode()
        {
            bool isEnabled = !JsonConfig.settings.darkMode;
            JsonConfig.settings.darkMode = isEnabled;
            MainMenu.darkModeItem.Checked = isEnabled;

            RunScheduler();
        }

        public void ToggleInterpolation()
        {
            bool isEnabled = !JsonConfig.settings.enableInterpolation;
            JsonConfig.settings.enableInterpolation = isEnabled;
            MainMenu.interpolateItem.Checked = isEnabled;

            RunScheduler();
        }

        public static DaySegment GetDaySegment(SolarData data, DateTime time)
        {
            if (data.polarPeriod == PolarPeriod.PolarDay)
            {
                return DaySegment.AllDay;
            }
            else if (data.polarPeriod == PolarPeriod.PolarNight)
            {
                return DaySegment.AllNight;
            }
            else if (data.solarTimes[0] <= time && time < data.solarTimes[1])
            {
                return DaySegment.Sunrise;
            }
            else if (data.solarTimes[1] <= time && time < data.solarTimes[2])
            {
                return DaySegment.Day;
            }
            else if (data.solarTimes[2] <= time && time < data.solarTimes[3])
            {
                return DaySegment.Sunset;
            }
            else
            {
                return DaySegment.Night;
            }
        }

        public SchedulerState GetImageData(SolarData data, ThemeConfig theme, DateTime current)
        {
            int[] imageList = null;
            DateTime segmentStart;
            DateTime segmentEnd;
            bool isSunUp = IsSunUp(data, current);

            SchedulerState imageData = new SchedulerState() { daySegment2 = isSunUp ? 0 : 1 };

            if (!JsonConfig.settings.darkMode)
            {
                switch (GetDaySegment(data, current))
                {
                    case DaySegment.AllDay:
                        imageList = theme?.dayImageList;
                        segmentStart = current.Date;
                        segmentEnd = current.Date.AddDays(1);
                        imageData.daySegment4 = 1;
                        break;
                    case DaySegment.AllNight:
                        imageList = theme?.nightImageList;
                        segmentStart = current.Date;
                        segmentEnd = current.Date.AddDays(1);
                        imageData.daySegment4 = 3;
                        break;
                    case DaySegment.Sunrise:
                        imageList = theme?.sunriseImageList;
                        segmentStart = data.solarTimes[0];
                        segmentEnd = data.solarTimes[1];
                        imageData.daySegment4 = 0;
                        break;
                    case DaySegment.Day:
                        imageList = theme?.dayImageList;
                        segmentStart = data.solarTimes[1];
                        segmentEnd = data.solarTimes[2];
                        imageData.daySegment4 = 1;
                        break;
                    case DaySegment.Sunset:
                        imageList = theme?.sunsetImageList;
                        segmentStart = data.solarTimes[2];
                        segmentEnd = data.solarTimes[3];
                        imageData.daySegment4 = 2;
                        break;
                    default:
                        imageList = theme?.nightImageList;
                        imageData.daySegment4 = 3;

                        if (current < data.solarTimes[0])
                        {
                            SolarData yesterdaysData = SunriseSunsetService.GetSolarData(current.Date.AddDays(-1));
                            segmentStart = yesterdaysData.solarTimes[3];
                            segmentEnd = data.solarTimes[0];
                        }
                        else
                        {
                            segmentStart = data.solarTimes[3];
                            SolarData tomorrowsData = SunriseSunsetService.GetSolarData(current.Date.AddDays(1));
                            segmentEnd = tomorrowsData.solarTimes[0];
                        }

                        break;
                }
            }
            else
            {
                imageList = theme?.nightImageList;

                if (data.polarPeriod != PolarPeriod.None)
                {
                    segmentStart = current.Date;
                    segmentEnd = current.Date.AddDays(1);
                }
                else if (isSunUp)
                {
                    segmentStart = data.sunriseTime;
                    segmentEnd = data.sunsetTime;
                }
                else if (current < data.sunriseTime)
                {
                    SolarData yesterdaysData = SunriseSunsetService.GetSolarData(current.Date.AddDays(-1));
                    segmentStart = yesterdaysData.sunsetTime;
                    segmentEnd = data.sunriseTime;
                }
                else
                {
                    segmentStart = data.sunsetTime;
                    SolarData tomorrowsData = SunriseSunsetService.GetSolarData(current.Date.AddDays(1));
                    segmentEnd = tomorrowsData.sunriseTime;
                }
            }

            if (imageList != null)
            {
                TimeSpan segmentLength = segmentEnd - segmentStart;
                TimeSpan timerLength = new TimeSpan(segmentLength.Ticks / imageList.Length);

                int imageNumber = (int)((current.Ticks - segmentStart.Ticks) / timerLength.Ticks);
                imageData.imageId = imageList[imageNumber];
                imageData.imageNumber = imageNumber;
                imageData.startTick = segmentStart.Ticks + timerLength.Ticks * imageNumber;
                imageData.endTick = segmentStart.Ticks + timerLength.Ticks * (imageNumber + 1);
            }

            return imageData;
        }

        private void SetWallpaper(int imageId)
        {
            string imageFilename = ThemeManager.currentTheme.imageFilename.Replace("*", imageId.ToString());
            string imagePath = Path.Combine(Directory.GetCurrentDirectory(), "themes",
                ThemeManager.currentTheme.themeId, imageFilename);

            if (imagePath == lastImagePath)
            {
                return;
            }

            WallpaperApi.EnableTransitions();
            UwpDesktop.GetHelper().SetWallpaper(imageFilename);

            lastImagePath = imagePath;
        }

        private void SetWallpaper(int imageId1, int imageId2, double percent)
        {
            string imageFilename1 = ThemeManager.currentTheme.imageFilename.Replace("*", imageId1.ToString());
            string imageFilename2 = ThemeManager.currentTheme.imageFilename.Replace("*", imageId2.ToString());

            string folder = Path.Combine(Directory.GetCurrentDirectory(), "themes", ThemeManager.currentTheme.themeId);
            string imagePath1 = Path.Combine(folder, imageFilename1);
            string imagePath2 = Path.Combine(folder, imageFilename2);
            string outputPath = Path.Combine(folder, "current.jpg");

            Task.Run(() =>
            {
                var sw = new System.Diagnostics.Stopwatch();

                sw.Restart();
                using (var image1 = new ImageMagick.MagickImage(imagePath1))
                using (var image2 = new ImageMagick.MagickImage(imagePath2))
                {
                    image1.Quality = 98;
                    image1.Composite(image2, ImageMagick.CompositeOperator.Blend, Math.Round(percent * 100).ToString());
                    image1.Write(outputPath);
                }
                sw.Stop();
                Console.WriteLine($"NEW: {sw.ElapsedMilliseconds}");

                GC.Collect();

                WallpaperApi.EnableTransitions();
                UwpDesktop.GetHelper().SetWallpaper("current.jpg");
            });

            lastImagePath = outputPath;
        }

        private void UpdateInterpolation()
        {
            lock (interpolationLock)
            {
                if (ThemeManager.currentTheme == null)
                {
                    Console.WriteLine("I: null");
                    return;
                }
                else if (interpolation.imageId1 == interpolation.imageId2)
                {
                    Console.WriteLine($"I: {interpolation.imageId1} == {interpolation.imageId2}");
                    SetWallpaper(interpolation.imageId1);
                    return;
                }

                long total = interpolation.endTick - interpolation.startTick;
                long current = DateTime.Now.Ticks - interpolation.startTick;
                double percent = Interpolation.Calculate((double)current / total, ThemeManager.currentTheme.interpolation);

                if (percent - interpolation.lastPercent < 0.01f)
                {
                    Console.WriteLine($"I: {percent} {interpolation.imageId1} => {interpolation.imageId2} (not enough)");
                    return;
                }
                else if (percent == 0)
                {
                    Console.WriteLine($"I: 0 {interpolation.imageId1}");
                    SetWallpaper(interpolation.imageId1);
                }
                else if (percent == 1)
                {
                    Console.WriteLine($"I: 1 {interpolation.imageId2}");
                    SetWallpaper(interpolation.imageId2);
                }
                else
                {
                    Console.WriteLine($"I: {percent} {interpolation.imageId1} => {interpolation.imageId2}");
                    SetWallpaper(interpolation.imageId1, interpolation.imageId2, percent);
                }

                interpolation.lastPercent = percent;
            }
        }

        private void StartTimer(DateTime futureTime)
        {
            long intervalTicks = futureTime.Ticks - DateTime.Now.Ticks;

            if (intervalTicks < timerError)
            {
                intervalTicks = 1;
            }

            TimeSpan interval = new TimeSpan(intervalTicks);

            schedulerTimer.Interval = interval.TotalMilliseconds;
            schedulerTimer.Start();
        }

        public void HandleTimerEvent(bool updateLocation)
        {
            if (JsonConfig.settings.fullScreenPause && fullScreenChecker.runningFullScreen)
            {
                fullScreenChecker.timerEventPending = true;
                return;
            }

            if (updateLocation && JsonConfig.settings.useWindowsLocation)
            {
                Task.Run(() => UwpLocation.UpdateGeoposition());
            }

            RunScheduler();
            UpdateChecker.TryCheckAuto();
        }

        private void OnBackgroundTimerElapsed(object sender, EventArgs e)
        {
            if (nextUpdateTime.HasValue && DateTime.Now >= nextUpdateTime.Value)
            {
                HandleTimerEvent(true);
            }
            else if (JsonConfig.settings.enableInterpolation)
            {
                UpdateInterpolation();
            }
        }

        private void OnSchedulerTimerElapsed(object sender, EventArgs e)
        {
            HandleTimerEvent(true);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                HandleTimerEvent(false);
            }
        }

        private void OnTimeChanged(object sender, EventArgs e)
        {
            HandleTimerEvent(false);
        }

        private static void CreateInterpolatedImage(string imagePath1, string imagePath2, string outputPath, float percent)
        {
            using (Bitmap image1 = new Bitmap(imagePath1))
            using (Bitmap image2 = new Bitmap(imagePath2))
            using (Bitmap output = new Bitmap(image1.Width, image1.Height, image1.PixelFormat))
            using (Graphics g = Graphics.FromImage(output))
            {
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.CompositingQuality = CompositingQuality.GammaCorrected;

                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(image1, 0, 0);

                ImageAttributes attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix() { Matrix33 = percent }, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);

                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImage(image2, new Rectangle(0, 0, image2.Width, image2.Height), 0, 0, image2.Width, image2.Height, GraphicsUnit.Pixel, attributes);

                output.Save(outputPath, ImageFormat.Jpeg);
            }
        }

        private static Image CloneImage(Image img)
        {
            Bitmap img2 = new Bitmap(img.Width, img.Height);
            using (Graphics g = Graphics.FromImage(img2))
            {
                g.DrawImageUnscaled(img, 0, 0);
            }
            return img2;
        }
    }
}
