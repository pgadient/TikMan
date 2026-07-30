using System.Runtime.InteropServices;
using Avalonia;

namespace TikMan.App.Avalonia;

/// <summary>Forces the software renderer on every backend. Each platform exposes its own options type, so
/// they all have to be set – whichever one the running platform uses is the one that takes effect.
/// <para>Software is listed as the <b>only</b> mode rather than as a fallback: a fallback would still let
/// the GPU path be tried first, which is exactly the path being avoided.</para></summary>
internal static class SoftwareRendering
{
    public static AppBuilder WithSoftwareRendering(this AppBuilder builder)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = new[] { AvaloniaNativeRenderingMode.Software },
            });

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
            });

        return builder.With(new X11PlatformOptions
        {
            RenderingMode = new[] { X11RenderingMode.Software },
        });
    }
}
