using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using System.Runtime.Serialization;

namespace KLF
{
    [Serializable]
    class KLFGlobalSettings
    {
        public float InfoDisplayWindowX;
        public float InfoDisplayWindowY;
        public float ScreenshotDisplayWindowX;
        public float ScreenshotDisplayWindowY;
        public float ChatDisplayWindowX;
        public float ChatDisplayWindowY;
        public bool InfoDisplayBig = false;

        public bool ChatWindowEnabled = false;
        public bool ChatWindowWide = false;
        public KeyCode GuiToggleKey = KeyCode.F7;
        public KeyCode ScreenshotKey = KeyCode.F8;
        public KeyCode ChatKey = KeyCode.F9;
        public KeyCode ViewKey = KeyCode.F10;

        [OptionalField(VersionAdded = 1)]
        public bool SmoothScreens = true;
        [OptionalField(VersionAdded = 2)]
        public bool ChatColors = true;
        [OptionalField(VersionAdded = 2)]
        public bool ShowInactiveShips = true;
        [OptionalField(VersionAdded = 2)]
        public bool ShowOtherShips = true;
        [OptionalField(VersionAdded = 3)]
        public bool ShowOrbits = true;

        [OnDeserializing]
        private void SetDefault(StreamingContext sc)
        {
            SmoothScreens = true;
            GuiToggleKey = KeyCode.F7;
            ScreenshotKey = KeyCode.F8;
            ChatKey = KeyCode.F9;
            ViewKey = KeyCode.F10;
            ChatColors = true;
            ShowInactiveShips = true;
            ShowOtherShips = true;
            ShowOrbits = true;
        }

        public static KLFGlobalSettings Instance = new KLFGlobalSettings();
    }
}
