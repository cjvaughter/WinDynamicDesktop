﻿// This Source Code Form is subject to the terms of the Mozilla Public
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

namespace WinDynamicDesktop
{
    public class SchedulerState
    {
        public int imageId;
        public int imageNumber;
        public long nextUpdateTicks;
        public int daySegment2;
        public int daySegment4;
    }

    class WallpaperChangeScheduler
    {
        private enum DaySegment { Sunrise, Day, Sunset, Night, AllDay, AllNight };

        private string lastImagePath;
        private DateTime? nextUpdateTime;

        public static bool isSunUp;
        public FullScreenApi fullScreenChecker;

        private Timer backgroundTimer = new Timer();
        private Timer schedulerTimer = new Timer();
        private const long timerError = (long)(TimeSpan.TicksPerMillisecond * 15.6);

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

        public void RunScheduler(bool forceImageUpdate = false)
        {
            if (!LaunchSequence.IsLocationReady() || !LaunchSequence.IsThemeReady())
            {
                return;
            }

            schedulerTimer.Stop();

            SolarData data = SunriseSunsetService.GetSolarData(DateTime.Today);
            isSunUp = (data.sunriseTime <= DateTime.Now && DateTime.Now < data.sunsetTime);
            DateTime? nextImageUpdateTime = null;

            if (ThemeManager.currentTheme != null)
            {
                if (forceImageUpdate)
                {
                    lastImagePath = null;
                }

                WallpaperShuffler.MaybeShuffleWallpaper();
            }

            SchedulerState imageData = GetImageData(data, ThemeManager.currentTheme);

            if (ThemeManager.currentTheme != null)
            {
                SetWallpaper(imageData.imageId);
                nextImageUpdateTime = new DateTime(imageData.nextUpdateTicks);
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
            bool isEnabled = JsonConfig.settings.darkMode ^ true;
            JsonConfig.settings.darkMode = isEnabled;
            MainMenu.darkModeItem.Checked = isEnabled;

            RunScheduler();
        }

        private DaySegment GetCurrentDaySegment(SolarData data)
        {
            if (data.polarPeriod == PolarPeriod.PolarDay)
            {
                return DaySegment.AllDay;
            }
            else if (data.polarPeriod == PolarPeriod.PolarNight)
            {
                return DaySegment.AllNight;
            }
            else if (data.solarTimes[0] <= DateTime.Now && DateTime.Now < data.solarTimes[1])
            {
                return DaySegment.Sunrise;
            }
            else if (data.solarTimes[1] <= DateTime.Now && DateTime.Now < data.solarTimes[2])
            {
                return DaySegment.Day;
            }
            else if (data.solarTimes[2] <= DateTime.Now && DateTime.Now < data.solarTimes[3])
            {
                return DaySegment.Sunset;
            }
            else
            {
                return DaySegment.Night;
            }
        }

        public SchedulerState GetImageData(SolarData data, ThemeConfig theme)
        {
            int[] imageList = null;
            DateTime segmentStart;
            DateTime segmentEnd;
            SchedulerState imageData = new SchedulerState() { daySegment2 = isSunUp ? 0 : 1 };

            if (!JsonConfig.settings.darkMode)
            {
                switch (GetCurrentDaySegment(data))
                {
                    case DaySegment.AllDay:
                        imageList = theme?.dayImageList;
                        segmentStart = DateTime.Today;
                        segmentEnd = DateTime.Today.AddDays(1);
                        imageData.daySegment4 = 1;
                        break;
                    case DaySegment.AllNight:
                        imageList = theme?.nightImageList;
                        segmentStart = DateTime.Today;
                        segmentEnd = DateTime.Today.AddDays(1);
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

                        if (DateTime.Now < data.solarTimes[0])
                        {
                            SolarData yesterdaysData = SunriseSunsetService.GetSolarData(DateTime.Today.AddDays(-1));
                            segmentStart = yesterdaysData.solarTimes[3];
                            segmentEnd = data.solarTimes[0];
                        }
                        else
                        {
                            segmentStart = data.solarTimes[3];
                            SolarData tomorrowsData = SunriseSunsetService.GetSolarData(DateTime.Today.AddDays(1));
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
                    segmentStart = DateTime.Today;
                    segmentEnd = DateTime.Today.AddDays(1);
                }
                else if (isSunUp)
                {
                    segmentStart = data.sunriseTime;
                    segmentEnd = data.sunsetTime;
                }
                else if (DateTime.Now < data.sunriseTime)
                {
                    SolarData yesterdaysData = SunriseSunsetService.GetSolarData(DateTime.Today.AddDays(-1));
                    segmentStart = yesterdaysData.sunsetTime;
                    segmentEnd = data.sunriseTime;
                }
                else
                {
                    segmentStart = data.sunsetTime;
                    SolarData tomorrowsData = SunriseSunsetService.GetSolarData(DateTime.Today.AddDays(1));
                    segmentEnd = tomorrowsData.sunriseTime;
                }
            }

            if (imageList != null)
            {
                TimeSpan segmentLength = segmentEnd - segmentStart;
                TimeSpan timerLength = new TimeSpan(segmentLength.Ticks / imageList.Length);

                int imageNumber = (int)((DateTime.Now - segmentStart).Ticks / timerLength.Ticks);
                imageData.imageId = imageList[imageNumber];
                imageData.imageNumber = imageNumber;
                imageData.nextUpdateTicks = segmentStart.Ticks + timerLength.Ticks * (imageNumber + 1);
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
    }
}
