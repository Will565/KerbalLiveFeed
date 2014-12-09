using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KLF
{
    class KLFInfoDisplay
    {//Singleton
        private static KLFInfoDisplay instance = null;
        public static KLFInfoDisplay Instance
        {
            get
            {
                if (instance == null)
                    instance = new KLFInfoDisplay();
                return instance;
            }
        }

        //Properties
        public const float WindowWidthMinimized = 60;
        public const float WindowWidthDefault = 250;
        public const float WindowWidthBig = 320;
        public const float WindowHeight = 360;
        public const float WindowHeightBig = 480;
        public const float WindowHeightMinimized = 64;

        public static bool InfoDisplayActive = true;
        public static bool InfoDisplayMinimized = false;
        public static bool InfoDisplayDetailed = false;
        public static bool InfoDisplayOptions = false;
        public static Rect InfoWindowPos =
            new Rect( 20
                    , Screen.height / 2 - WindowHeight / 2
                    , WindowWidthDefault
                    , WindowHeight);
        public static GUILayoutOption[] LayoutOptions;
        public static Vector2 InfoScrollPos = Vector2.zero;
    }
}
