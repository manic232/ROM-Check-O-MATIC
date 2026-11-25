// ROM Check O-MATIC - the pop-up tips shown when the mouse rests on a button, tick box or field.
// One tip object serves every window, so a single setting (Options > Show Pop-up Tips) turns them all on or off.

using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;

namespace RomCheckOMatic
{
    static class Tips
    {
        const int Stay = 32767;   // how long a tip stays up, in milliseconds: the longest Windows allows (about 30 seconds)
        static readonly ToolTip tip = new ToolTip { AutoPopDelay = Stay, InitialDelay = 700, ReshowDelay = 200 };
        static bool enabled = true;
        static readonly List<ToolStrip> menus = new List<ToolStrip>();

        public static bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                tip.Active = value;
                foreach (var m in menus) m.ShowItemToolTips = value;
            }
        }

        // Menus show their items' tips too, following the same setting.
        public static void Menu(ToolStrip menu)
        {
            if (!menus.Contains(menu)) menus.Add(menu);
            menu.ShowItemToolTips = enabled;
            // A menu shows its tips with its own tip object, which closes them after 5 seconds. It isn't public, so reach it by name.
            try
            {
                var own = typeof(ToolStrip).GetProperty("ToolTip", BindingFlags.NonPublic | BindingFlags.Instance);
                var menuTip = own == null ? null : own.GetValue(menu, null) as ToolTip;
                if (menuTip != null) menuTip.AutoPopDelay = Stay;
            }
            catch { }
        }

        // A menu item's tip; hands the item back.
        public static T Item<T>(T item, string text) where T : ToolStripItem { item.ToolTipText = text; return item; }

        public static void Set(Control c, string text) { tip.SetToolTip(c, text); }

        // For controls made in a loop or a helper: sets the tip and hands the control back.
        public static T With<T>(T c, string text) where T : Control { tip.SetToolTip(c, text); return c; }
    }
}
