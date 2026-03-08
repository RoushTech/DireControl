namespace DireControl.Enums;

public enum RadarProvider
{
    /// <summary>IEM NEXRAD composite — free, US coverage, zoom 8, 5-minute updates.</summary>
    IemNexrad,

    /// <summary>RainViewer free tier — free, global, zoom 7, 10-minute updates.</summary>
    RainViewer,

    /// <summary>RainViewer Pro — paid ($40/yr), global, zoom 12, 10-minute updates.</summary>
    RainViewerPro,
}
