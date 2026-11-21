using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;

namespace ApiTester.Views;

public sealed class TextBoxSearchHighlightOverlay : Control
{
    private const int MaxRenderedMatchesPerFrame = 400;

    private static readonly IBrush MatchFillBrush = new SolidColorBrush(Color.FromArgb(76, 79, 140, 255));
    private static readonly IPen MatchBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(145, 79, 140, 255)), 1);
    private static readonly IBrush ActiveFillBrush = new SolidColorBrush(Color.FromArgb(142, 37, 99, 235));
    private static readonly IPen ActiveBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(245, 147, 197, 253)), 1.5);

    private readonly List<(int Start, int Length)> _matches = [];
    private TextPresenter? _presenter;
    private ScrollViewer? _scrollViewer;
    private int _activeIndex = -1;

    public void Attach(TextPresenter? presenter, ScrollViewer? scrollViewer)
    {
        _presenter = presenter;
        _scrollViewer = scrollViewer;
        InvalidateVisual();
    }

    public void SetSearchState(IReadOnlyList<(int Start, int Length)> matches, int activeIndex)
    {
        _matches.Clear();
        if (matches.Count > 0)
        {
            _matches.AddRange(matches);
        }

        _activeIndex = activeIndex;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_presenter is null || _scrollViewer is null || _matches.Count == 0)
        {
            return;
        }

        var textLayout = _presenter.TextLayout;
        var presenterText = _presenter.Text;
        if (textLayout is null || string.IsNullOrEmpty(presenterText))
        {
            return;
        }

        var presenterOriginOpt = _presenter.TranslatePoint(new Point(0, 0), this);
        var viewportOriginOpt = _scrollViewer.TranslatePoint(new Point(0, 0), this);
        if (!presenterOriginOpt.HasValue || !viewportOriginOpt.HasValue)
        {
            return;
        }

        var viewportSize = _scrollViewer.Viewport;
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            return;
        }

        var presenterOrigin = presenterOriginOpt.Value;
        var clipRect = new Rect(viewportOriginOpt.Value, viewportSize);

        var firstIndex = 0;
        var lastIndex = _matches.Count - 1;

        var topLeftInPresenter = _scrollViewer.TranslatePoint(new Point(0, 0), _presenter);
        var bottomRightInPresenter = _scrollViewer.TranslatePoint(
            new Point(viewportSize.Width, viewportSize.Height),
            _presenter);

        if (topLeftInPresenter.HasValue && bottomRightInPresenter.HasValue)
        {
            var minVisibleChar = Math.Max(0, textLayout.HitTestPoint(topLeftInPresenter.Value).TextPosition - 256);
            var maxVisibleChar = Math.Min(presenterText.Length, textLayout.HitTestPoint(bottomRightInPresenter.Value).TextPosition + 256);

            firstIndex = FindFirstMatchEndingAfter(minVisibleChar);
            if (firstIndex >= _matches.Count)
            {
                return;
            }

            lastIndex = FindLastMatchStartingBefore(maxVisibleChar, firstIndex);
            if (lastIndex < firstIndex)
            {
                return;
            }
        }

        using (context.PushClip(clipRect))
        {
            var rendered = 0;
            for (var i = firstIndex; i <= lastIndex && rendered < MaxRenderedMatchesPerFrame; i++)
            {
                var (start, length) = _matches[i];
                if (length <= 0 || start < 0 || start + length > presenterText.Length)
                {
                    continue;
                }

                var isActive = i == _activeIndex;
                var fill = isActive ? ActiveFillBrush : MatchFillBrush;
                var pen = isActive ? ActiveBorderPen : MatchBorderPen;

                foreach (var rect in textLayout.HitTestTextRange(start, length))
                {
                    var drawRect = new Rect(
                        rect.X + presenterOrigin.X,
                        rect.Y + presenterOrigin.Y,
                        Math.Max(2, rect.Width),
                        Math.Max(1, rect.Height));

                    if (!drawRect.Intersects(clipRect))
                    {
                        continue;
                    }

                    context.DrawRectangle(fill, pen, drawRect, radiusX: 3, radiusY: 3);
                }

                rendered++;
            }
        }
    }

    private int FindFirstMatchEndingAfter(int minChar)
    {
        var lo = 0;
        var hi = _matches.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            var m = _matches[mid];
            if (m.Start + m.Length < minChar)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private int FindLastMatchStartingBefore(int maxChar, int startIndex)
    {
        var lo = startIndex;
        var hi = _matches.Count - 1;
        var best = startIndex - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_matches[mid].Start <= maxChar)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }
}
