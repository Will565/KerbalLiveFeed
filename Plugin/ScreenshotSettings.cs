using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


public class ScreenshotSettings
{

    public const float DefaultAspectRatio = 16.0f / 9.0f;
    public const int DefaultMinHeight = 135;
    public const int DefaultMaxHeight = 600;
    public const int DefaultMinWidth = (int)(DefaultMinHeight * DefaultAspectRatio);
    public const int DefaultMaxWidth = (int)(DefaultMaxHeight * DefaultAspectRatio);
    private int maxWidth;
    public int MaxWidth
    {
        set
        {
            maxWidth = Math.Min(Math.Max(value, DefaultMinWidth), DefaultMaxWidth);
            maxHeight = (int)Math.Round(MaxWidth / DefaultAspectRatio);
        }
        get
        {
            return maxWidth;
        }
    }
    private int maxHeight;
    public int MaxHeight
    {
        set
        {
            maxHeight = Math.Min(Math.Max(value, DefaultMinHeight), DefaultMaxHeight);
            maxWidth = (int)Math.Round(MaxHeight * DefaultAspectRatio);
        }
        get
        {
            return maxHeight;
        }
    }

    /* constructor */
    public ScreenshotSettings()
    {
        maxHeight = 270;//default
    }

    public void GetBoundedDimensions
        ( int width
        , int height
        , ref int boundedWidth
        , ref int boundedHeight
        )
    {
        float aspect = (float)width / (float)height;
        if (aspect > DefaultAspectRatio)
        {//Wider than ideal aspect ratio
            boundedWidth = MaxWidth;
            boundedHeight = Math.Min
                (MaxHeight, (int)Math.Round(MaxWidth / aspect));
        }
        else
        {//Taller than ideal aspect ratio
            boundedHeight = MaxHeight;
            boundedWidth = Math.Min
                (MaxWidth, (int)Math.Round(MaxHeight * aspect));
        }
    }

    public int MaxNumBytes
    {
        get
        {
            return MaxWidth * MaxHeight * 3;
        }
    }
}
