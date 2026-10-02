using System;
using System.Drawing;
using System.IO;

namespace ClaudeSessionDeck
{
    /// <summary>The application icon (icon\app.ico, embedded by build.ps1), picked at the size the caller needs.</summary>
    internal static class AppIcon
    {
        private const string ResourceName = "ClaudeSessionDeck.app.ico";

        public static Icon Load(Size size)
        {
            try
            {
                using (Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream != null) { return new Icon(stream, size); }
                }
            }
            catch (Exception ex)
            {
                Hook.Log("icon", "cannot load the embedded icon: " + ex.Message);
            }
            return SystemIcons.Application; // e.g. the unit-test build, which embeds no resources
        }
    }
}
