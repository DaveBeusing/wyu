using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

internal sealed partial class AlphaOverlay
{
    private const string ConsoleTitle = "WYU world console";
    private volatile ConsoleView? _console;

    internal ConsoleView? Console { set => _console = value; }

    private sealed class ConsolePanel(nint handle)
    {
        private (ConsoleView View, Point Origin, Size Size)? _drawn;

        internal void Hide()
        {
            if (_drawn is null)
                return;
            _ = ShowWindow(handle, 0);
            _ = SetWindowText(handle, ConsoleTitle);
            _drawn = null;
        }

        internal void Update(ConsoleView view, Point origin, Size size)
        {
            var layout = (view, origin, size);
            if (_drawn == layout)
                return;
            var geometry = LayoutConsole(view, size.Height);
            var rows = VisibleConsoleRows(view, size.Height, geometry);
            if (!DrawConsole(handle, view, rows, origin, size, geometry))
                return;
            var prompt = view.SearchingHistory ? $"History search: {view.Prompt} | {view.Suggestions}" : $"> {view.Prompt}";
            SetWindowText(handle, $"{ConsoleTitle} | {string.Join(" | ", rows.AsSpan())} | {prompt}");
            if (_drawn is null)
                _ = ShowWindow(handle, 4);
            _drawn = layout;
        }
    }

    private readonly record struct ConsoleGeometry(int Top, int Footer, int Row, int Font, bool SingleHeader = false)
    {
        internal int RowsAt(int height) => Math.Max(0, (height - Top - Footer - 2) / Row);
    }

    private static ConsoleGeometry LayoutConsole(ConsoleView view, int height)
    {
        ConsoleGeometry standard = height switch
        {
            < 268 => new(48, 56, 16, 13),
            < 304 => new(48, 56, 18, 15),
            _ => new(59, 63, 20, 15),
        };
        if (standard.RowsAt(height) >= view.RequiredRows)
            return standard;

        return height switch
        {
            < 261 => new(28, 55, 15, 12, SingleHeader: true),
            < 283 => new(28, 55, 16, 13, SingleHeader: true),
            _ => new(28, 55, 18, 15, SingleHeader: true),
        };
    }

    private static ArraySegment<string> VisibleConsoleRows(ConsoleView view, int height, ConsoleGeometry geometry)
    {
        var count = Math.Min(view.Lines.Length, geometry.RowsAt(height));
        var first = view.ShowingHelp ? 0 : view.Lines.Length - count;
        return new(view.Lines, first, count);
    }

    private static bool DrawConsole(nint handle, ConsoleView view, ArraySegment<string> rows, Point origin, Size size,
        ConsoleGeometry geometry) =>
        DrawLayer(handle, origin, size, 247, 2, dc =>
        {
            const uint Background = 0x00251C16;
            const uint Input = 0x00332920;
            const uint Accent = 0x00D9DF87;
            const uint Text = 0x00F8F0E5;
            const uint Muted = 0x00BDAE9D;
            const int Padding = 16;

            if (!PaintRect(dc, 0, 0, size.Width, size.Height, Background) ||
                !PaintRect(dc, 0, 0, size.Width, 2, Accent) || SetBkMode(dc, 1) == 0)
                return false;

            var font = CreateFont(-geometry.Font, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Consolas");
            var previous = font == 0 ? 0 : SelectObject(dc, font);
            try
            {
                var title = view.SearchingHistory ? "HISTORY SEARCH" : "WORLD CONSOLE";
                if (geometry.SingleHeader)
                {
                    var heading = size.Width >= 620
                        ? view.SearchingHistory ? "HISTORY SEARCH   Ctrl+R older   Enter edit   Esc cancel"
                            : "WORLD CONSOLE   Esc / F3 close   Tab complete   Ctrl+R search"
                        : view.SearchingHistory ? "SEARCH   Ctrl+R Enter Esc" : "CONSOLE   Esc Tab Ctrl+R";
                    DrawLine(heading, Padding, 9, Accent);
                }
                else
                {
                    var compact = geometry.Row < 20;
                    DrawLine(title, Padding, compact ? 10 : 12, Accent);
                    var controls = view.SearchingHistory
                        ? size.Width < 510 ? "Ctrl+R · Enter · Esc" : "Ctrl+R older   Enter edit   Esc cancel"
                        : size.Width < 620 ? "Esc · Tab · Ctrl+R · ↑↓"
                            : "Esc / F3 close   Tab complete   Ctrl+R search   ↑↓ history   Enter run";
                    DrawLine(controls, Padding, compact ? 29 : 34, Muted);
                }

                var promptY = size.Height - geometry.Footer;
                _ = PaintRect(dc, 8, promptY, size.Width - 8, promptY + 28, Input);
                for (var i = 0; i < rows.Count; i++)
                    DrawLine(rows[i], Padding, geometry.Top + i * geometry.Row, Text);

                var available = Math.Max(1, (size.Width - 2 * Padding - 20) / 9);
                var caret = view.Prompt.IndexOf('▏');
                var start = Math.Max(0, caret - available + 1);
                DrawLine((view.SearchingHistory ? "? " : "> ") + view.Prompt[start..], Padding, promptY + 5, Accent);
                DrawLine(view.Suggestions ?? "help [cmd] · clear", Padding, promptY + 35, Muted);
                return true;
            }
            finally
            {
                if (previous != 0)
                    _ = SelectObject(dc, previous);
                if (font != 0)
                    _ = DeleteObject(font);
            }

            void DrawLine(string text, int left, int top, uint color)
            {
                if (size.Width <= left + Padding || top >= size.Height)
                    return;
                _ = SetTextColor(dc, color);
                var bounds = new Rect { Left = left, Top = top, Right = size.Width - Padding, Bottom = size.Height };
                _ = DrawText(dc, text, text.Length, ref bounds, 0x00008820); // Single line, ellipsis, no mnemonics.
            }
        });

    private static bool PaintRect(nint dc, int left, int top, int right, int bottom, uint color)
    {
        var brush = CreateSolidBrush(color);
        if (brush == 0)
            return false;
        try
        {
            var rect = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
            return FillRect(dc, ref rect, brush) != 0;
        }
        finally
        {
            _ = DeleteObject(brush);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, ref Rect bounds, nint brush);
}
