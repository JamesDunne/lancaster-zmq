using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WellDunne.LanCaster.GUI
{
    /// <summary>
    /// Interaction logic for SectorCompletionBar.xaml
    /// </summary>
    public partial class SectorCompletionBar : FrameworkElement
    {
        public SectorCompletionBar()
        {
            this._visual = CreateVisual();
            this.AddVisualChild(this._visual);
            this.AddLogicalChild(this._visual);
        }

        private DrawingVisual _visual;
        private BitArray _completion = null;
        private int _currentSector = 0;

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            RenderMyVisual(this._visual);
            InvalidateVisual();
        }

        public void UpdateProgress(BitArray completion, int currentSector)
        {
            this._completion = completion;
            this._currentSector = currentSector;
            RenderMyVisual(this._visual);
            InvalidateVisual();
        }

        private DrawingVisual CreateVisual()
        {
            var v = new DrawingVisual();
            RenderMyVisual(v);
            return v;
        }

        private void RenderMyVisual(DrawingVisual v)
        {
            using (var dc = v.RenderOpen())
            {
                RenderRectangles(dc);
            }
        }

        private void RenderRectangles(DrawingContext drawingContext)
        {
            // Render a vertical gray gradient in a rounded rectangle for the background:
            GradientStopCollection gsGray = new GradientStopCollection(
                new GradientStop[3] {
                    new GradientStop(Colors.LightGray, 0.0d),
                    new GradientStop(Colors.Silver, 0.5d),
                    new GradientStop(Colors.DarkGray, 1.0d),
                }
            );
            drawingContext.DrawRoundedRectangle(
                new LinearGradientBrush(gsGray, 90.0),
                new Pen(Brushes.Silver, 1.0),
                new Rect(0.0, 0.0, this.ActualWidth, this.ActualHeight),
                1.0,
                1.0
            );

            BitArray tmp = this._completion;
            if (tmp == null) return;

            GradientStopCollection gsBlue = new GradientStopCollection(
                new GradientStop[3] {
                    new GradientStop(Colors.LightBlue, 0.0d),
                    new GradientStop(Color.FromRgb(48, 48, 240), 0.5d),
                    new GradientStop(Colors.DarkBlue, 1.0d),
                }
            );
            Brush blueGradBrush = new LinearGradientBrush(gsBlue, 90.0);
            Pen bluePen = new Pen(blueGradBrush, 1.0d);

            // Determine how wide each sector should be represented as:
            double secWidth = this.ActualWidth / (double)_completion.Length;
            double top = this.ActualHeight * 0.05;
            double height = this.ActualHeight * 0.9;

            bool ones = false;
            int starti = 0;

            for (int i = 0; i < tmp.Length; ++i)
            {
                if (!ones)
                {
                    if (!tmp[i]) continue;

                    starti = i;
                    ones = true;
                }
                else
                {
                    if (tmp[i]) continue;

                    ones = false;
                    // Draw a blue sector rectangle for all non-completed sectors up to here:
                    drawingContext.DrawRoundedRectangle(blueGradBrush, bluePen, new Rect(starti * secWidth, top, (i - starti) * secWidth, height), 1.0, 1.0);
                }
            }

            // Last remaining sector range:
            if (ones)
            {
                // Draw a blue sector rectangle for all non-completed sectors up to here:
                drawingContext.DrawRoundedRectangle(blueGradBrush, bluePen, new Rect(starti * secWidth, top, (tmp.Length - starti) * secWidth, height), 1.0, 1.0);
            }

            // Only draw the current sector selector if applicable:
            if ((this._currentSector >= 0) && (this._currentSector < tmp.Length))
            {
                // Draw the current sector pointer as a rounded rectangle around the sector:
                drawingContext.DrawRoundedRectangle(
                    null,
                    new Pen(Brushes.Red, 2.0d),
                    new Rect(this._currentSector * secWidth, 0.0d, secWidth, this.ActualHeight),
                    1.0,
                    1.0
                );
            }
        }

        protected override int VisualChildrenCount { get { return 1; } }

        protected override Visual GetVisualChild(int index)
        {
            return this._visual;
        }
    }
}
