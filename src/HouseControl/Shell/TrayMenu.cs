using System.Drawing;
using static HouseControl.Native.Win32;

namespace HouseControl.Shell;

/// <summary>
/// The tray icon's right-click menu, as a real Windows menu: it looks like every other menu of
/// the notification area, follows the system theme, and works with the keyboard.
/// </summary>
internal sealed class TrayMenu
{
    private abstract record Item;
    private sealed record Command(string Text, Action? Run, bool Checked, bool Default) : Item;
    private sealed record Line : Item;
    private sealed record Sub(string Text, IReadOnlyList<(string Text, bool Checked, Action? Run)> Items) : Item;

    private readonly List<Item> _items = new();

    public void Add(string text, Action run, bool isChecked = false, bool isDefault = false) =>
        _items.Add(new Command(text, run, isChecked, isDefault));

    public void Separator() => _items.Add(new Line());

    public void Submenu(string text, IEnumerable<(string Text, bool Checked, Action? Run)> items) =>
        _items.Add(new Sub(text, items.ToList()));

    public void Show(IntPtr owner, Point at)
    {
        var actions = new List<Action?> { null };
        var menu = CreatePopupMenu();
        var submenus = new List<IntPtr>();
        try
        {
            uint defaultId = 0;
            foreach (var item in _items)
            {
                switch (item)
                {
                    case Command c:
                        actions.Add(c.Run);
                        AppendMenu(menu, MF_STRING | (c.Checked ? MF_CHECKED : 0), new IntPtr(actions.Count - 1), c.Text);
                        if (c.Default) defaultId = (uint)(actions.Count - 1);
                        break;
                    case Line:
                        AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                        break;
                    case Sub s:
                        var sub = CreatePopupMenu();
                        submenus.Add(sub);
                        foreach (var (text, isChecked, run) in s.Items)
                        {
                            actions.Add(run);
                            AppendMenu(sub, MF_STRING | (isChecked ? MF_CHECKED : 0) | (run is null ? MF_GRAYED : 0),
                                new IntPtr(actions.Count - 1), text);
                        }
                        AppendMenu(menu, MF_POPUP, sub, s.Text);
                        break;
                }
            }
            if (defaultId != 0) SetMenuDefaultItem(menu, defaultId, 0);
            // The menu closes when clicked away only if its owner is the foreground window.
            SetForegroundWindow(owner);
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, at.X, at.Y, owner, IntPtr.Zero);
            PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen < actions.Count) actions[chosen]?.Invoke();
        }
        finally
        {
            DestroyMenu(menu); // destroys the submenus with it
        }
    }
}
