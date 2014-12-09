using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KLF
{
    class KLFScreenshotDisplay
    {
        public const float MinWindowWidth = ScreenshotSettings.DefaultMinWidth + 100;
        public const float MinWindowHeight = ScreenshotSettings.DefaultMinHeight + 10;

        public static Screenshot Screenshot = new Screenshot();
        public static ScreenshotSettings Settings = new ScreenshotSettings();
        public static bool WindowEnabled = false;
        public static String WatchPlayerName = String.Empty;
        public static int WatchPlayerIndex = 0;
        public static Texture2D Texture;

        public static Rect WindowPos =
            new Rect( Screen.width / 2 - MinWindowWidth / 2
                    , Screen.height / 2 - MinWindowHeight / 2
                    , MinWindowWidth
                    , MinWindowHeight);
        public static Vector2 ScrollPos = Vector2.zero;
        public static GUILayoutOption[] LayoutOptions;
    }
}
