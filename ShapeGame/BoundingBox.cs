using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Kinect;
using System.Windows;


namespace ShapeGame
{
    class BoundingBox
    {
        Double m_BoxX1_MIN;
        Double m_BoxY1_MIN;
        Double m_BoxX2_MAX;
        Double m_BoxY2_MAX;



        private Rect playerBounds;
        private System.Windows.Point playerCenter;
        private double playerScale;



        public BoundingBox(Double boxX1_MIN, Double boxY1_MIN, Double boxX2_MAX, Double boxY2_MAX)
        {
            m_BoxX1_MIN = boxX1_MIN;
            m_BoxY1_MIN = boxY1_MIN;
            m_BoxX2_MAX = boxX2_MAX;
            m_BoxY2_MAX = boxY2_MAX;
        }

        public Double GetX1MIN()
        {
            return m_BoxX1_MIN;
        }
        public Double GetY1MIN()
        {
            return m_BoxY1_MIN;
        }
        public Double GetX2MAX()
        {
            return m_BoxX2_MAX;
        }
        public Double GetY2MAX()
        {
            return m_BoxY2_MAX;
        }

        public void SetBounds(Rect r)
        {
            this.playerBounds = r;
            this.playerCenter.X = (this.playerBounds.Left + this.playerBounds.Right) / 2;
            this.playerCenter.Y = (this.playerBounds.Top + this.playerBounds.Bottom) / 2;
            this.playerScale = Math.Min(this.playerBounds.Width, this.playerBounds.Height / 2);
        }

        public bool IsBounced( Skeleton skeleton )
        {
            // 중복이지만 뭐 안전해서 나쁠건 없지...
            if (SkeletonTrackingState.Tracked == skeleton.TrackingState)
            {
                foreach (Joint joint in skeleton.Joints)
                {

                    if( AABB( joint ) )
                    {
                        return true;
                    }

                }
            }

            return false;
        }


        private bool AABB(Joint joint)
        {
            Double jointX = joint.Position.X;
            Double jointY = joint.Position.Y;

            jointX = (jointX * this.playerScale) + this.playerCenter.X;
            jointY = this.playerCenter.Y - (jointY * this.playerScale);

            if (JointTrackingState.Tracked == joint.TrackingState)
            {

                if (jointX < m_BoxX2_MAX && jointX > m_BoxX1_MIN)
                {
                    if (jointY < m_BoxY2_MAX && jointY > m_BoxY1_MIN)
                    {
                        return true;
                    }
                }
            }


            return false;
        }
    }
}
