// ROM Check O-MATIC - the pop-up tips shown when the mouse rests on a button, tick box or field.
// One tip object serves every window, so a single setting (Theme > Show Pop-up Tips) turns them all on or off.

using System.Windows.Forms;

namespace RomCheckOMatic
{
    static class Tips
    {
        static readonly ToolTip tip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 700, ReshowDelay = 200 };
        static bool enabled = true;

        public static bool Enabled
        {
            get { return enabled; }
            set { enabled = value; tip.Active = value; }
        }

        public static void Set(Control c, string text) { tip.SetToolTip(c, text); }

        // For controls made in a loop or a helper: sets the tip and hands the control back.
        public static T With<T>(T c, string text) where T : Control { tip.SetToolTip(c, text); return c; }
    }
}
