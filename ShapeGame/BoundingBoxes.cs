using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Kinect;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;

namespace ShapeGame
{
    class BoundingBoxes
    {
        private List<BoundingBox> boxes = new List<BoundingBox>();
        private bool IsBounce = false;



        private Rect playerBounds;
        private System.Windows.Point playerCenter;
        private double playerScale;

        private System.Windows.Media.Brush boxBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 100, 100));

        public void SetBounds(Rect r)
        {

            this.playerBounds = r;
            this.playerCenter.X = (this.playerBounds.Left + this.playerBounds.Right) / 2;
            this.playerCenter.Y = (this.playerBounds.Top + this.playerBounds.Bottom) / 2;
            this.playerScale = Math.Min(this.playerBounds.Width, this.playerBounds.Height / 2);
        }

        public void AddBox(Double boxX1_MIN, Double boxY1_MIN, Double boxX2_MAX, Double boxY2_MAX)
        {
            BoundingBox box = new BoundingBox(boxX1_MIN, boxY1_MIN, boxX2_MAX, boxY2_MAX);
            box.SetBounds(this.playerBounds);
            boxes.Add( box );
        }

        public void ResetBox()
        {
            IsBounce = false;
            boxes.Clear();
        }

        public int GetTotalBoxNum()
        {
            return boxes.Count;
        }

        //전부 충돌처리 되어야지만 인정...
        public bool IsBounced( Skeleton skeleton )
        {
            for (int i = 0; i < this.boxes.Count; i++)
            {
                BoundingBox boxes = this.boxes[i];
                if( !boxes.IsBounced( skeleton ) )
                {
                    IsBounce = false;
                    return false;
                }
            }

            IsBounce = true;
            return true;
        }



        public bool CheckBounced()
        {
            return IsBounce;
        }



        // 현재 사용 안함
        public void DrawBoxes(UIElementCollection children)
        {
            for (int i = 0; i < this.boxes.Count; i++)
            {
                BoundingBox boxes = this.boxes[i];

                
                var line_left = new Line
                {
                    StrokeThickness = 4,
                    X1 = boxes.GetX1MIN(),
                    Y1 = boxes.GetY1MIN(),
                    X2 = boxes.GetX1MIN(),
                    Y2 = boxes.GetY2MAX(),
                    Stroke = this.boxBrush,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeStartLineCap = PenLineCap.Round
                };
                children.Add(line_left);

                var line_right = new Line
                {
                    StrokeThickness = 4,
                    X1 = boxes.GetX2MAX(),
                    Y1 = boxes.GetY1MIN(),
                    X2 = boxes.GetX2MAX(),
                    Y2 = boxes.GetY2MAX(),
                    Stroke = this.boxBrush,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeStartLineCap = PenLineCap.Round
                };
                children.Add(line_right);


                var line_top = new Line
                {
                    StrokeThickness = 4,
                    X1 = boxes.GetX1MIN(),
                    Y1 = boxes.GetY1MIN(),
                    X2 = boxes.GetX2MAX(),
                    Y2 = boxes.GetY1MIN(),
                    Stroke = this.boxBrush,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeStartLineCap = PenLineCap.Round
                };
                children.Add(line_top);

                var line_bottom = new Line
                {
                    StrokeThickness = 4,
                    X1 = boxes.GetX1MIN(),
                    Y1 = boxes.GetY2MAX(),
                    X2 = boxes.GetX2MAX(),
                    Y2 = boxes.GetY2MAX(),
                    Stroke = this.boxBrush,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeStartLineCap = PenLineCap.Round
                };
                children.Add(line_bottom);


            }
        }
    }
}
