//------------------------------------------------------------------------------
// <copyright file="MainWindow.xaml.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

// This module contains code to do Kinect NUI initialization,
// processing, displaying players on screen, and sending updated player
// positions to the game portion for hit testing.

namespace ShapeGame
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Linq;
    using System.Media;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Threading;
    using Microsoft.Kinect;
    using ShapeGame.Speech;
    using ShapeGame.Utils;

    using System.Windows.Media;
    using System.Windows.Media.Imaging;

    using System.Resources;
    using System.Drawing;
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 
    public partial class MainWindow : Window
    {
        #region Private State
        private const int TimerResolution = 2;  // ms
        private const int NumIntraFrames = 3;
        private const int MaxShapes = 80;
        private const double MaxFramerate = 70;
        private const double MinFramerate = 15;
        private const double MinShapeSize = 12;
        private const double MaxShapeSize = 90;
        private const double DefaultDropRate = 2.5;
        private const double DefaultDropSize = 32.0;
        private const double DefaultDropGravity = 2;

        private readonly Dictionary<int, Player> players = new Dictionary<int, Player>();
        private readonly SoundPlayer popSound = new SoundPlayer();
        private readonly SoundPlayer hitSound = new SoundPlayer();
        private readonly SoundPlayer squeezeSound = new SoundPlayer();

        private readonly SoundPlayer openingSound = new SoundPlayer();
        private readonly SoundPlayer calibrateSound = new SoundPlayer();
        private readonly SoundPlayer stage1Sound = new SoundPlayer();
        private readonly SoundPlayer stage2Sound = new SoundPlayer();
        private readonly SoundPlayer stage3Sound = new SoundPlayer();
        private readonly SoundPlayer stage4Sound = new SoundPlayer();
        private readonly SoundPlayer successSound = new SoundPlayer();
        private readonly SoundPlayer goalSound = new SoundPlayer();
        private readonly SoundPlayer resultSound = new SoundPlayer();


        private double dropRate = DefaultDropRate;
        private double dropSize = DefaultDropSize;
        private double dropGravity = DefaultDropGravity;
        private DateTime lastFrameDrawn = DateTime.MinValue;
        private DateTime predNextFrame = DateTime.MinValue;
        private double actualFrameTime;

        private Skeleton[] skeletonData;

        // Player(s) placement in scene (z collapsed):
        private Rect playerBounds;
        private Rect screenRect;

        private double targetFramerate = MaxFramerate;
        private int frameCount;
        private bool runningGameThread;
        private FallingThings myFallingThings;
        private int playersAlive;


        private BoundingBoxes myBoundingBoxes;













        #endregion Private State



        #region ctor + Window Events
        public MainWindow()
        {
            InitializeComponent();
            this.RestoreWindowState();
        }

        // Since the timer resolution defaults to about 10ms precisely, we need to
        // increase the resolution to get framerates above between 50fps with any
        // consistency.
        [DllImport("Winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern int TimeBeginPeriod(uint period);

        private void RestoreWindowState()
        {
            // Restore window state to that last used
            Rect bounds = Properties.Settings.Default.PrevWinPosition;
            if (bounds.Right != bounds.Left)
            {
                this.Top = bounds.Top;
                this.Left = bounds.Left;
                this.Height = bounds.Height;
                this.Width = bounds.Width;
            }

            this.WindowState = (WindowState)Properties.Settings.Default.WindowState;
        }

        private void WindowLoaded(object sender, EventArgs e)
        {
            playfield.ClipToBounds = true;

            this.myFallingThings = new FallingThings(MaxShapes, this.targetFramerate, NumIntraFrames);
            this.myBoundingBoxes = new BoundingBoxes();

            this.UpdatePlayfieldSize();

            this.myFallingThings.SetGravity(this.dropGravity);
            this.myFallingThings.SetDropRate(this.dropRate);
            this.myFallingThings.SetSize(this.dropSize);
            this.myFallingThings.SetPolies(PolyType.All);
            this.myFallingThings.SetGameMode(GameMode.Off);

            SensorChooser.KinectSensorChanged += this.SensorChooserKinectSensorChanged;

            this.popSound.Stream = Properties.Resources.Pop_5;
            this.hitSound.Stream = Properties.Resources.Hit_2;
            this.squeezeSound.Stream = Properties.Resources.Squeeze;

            this.openingSound.Stream = Properties.Resources.opening;
            this.calibrateSound.Stream = Properties.Resources.calibrate_loop;
            this.stage1Sound.Stream = Properties.Resources.stage1;
            this.stage2Sound.Stream = Properties.Resources.stage2;
            this.stage3Sound.Stream = Properties.Resources.stage3;
            this.stage4Sound.Stream = Properties.Resources.stage4;
            this.successSound.Stream = Properties.Resources.success;
            this.goalSound.Stream = Properties.Resources.goal;
            this.resultSound.Stream = Properties.Resources.result_scene;

            //this.popSound.Play();


            ImageLoader();

            TimeBeginPeriod(TimerResolution);

            System.Windows.Threading.DispatcherTimer dispatcherTimer = new System.Windows.Threading.DispatcherTimer();
            dispatcherTimer.Tick += new EventHandler(dispatcherTimer_Tick);
            dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 100);
            dispatcherTimer.Start();

            
            var myGameThread = new Thread(this.GameThread);
            myGameThread.SetApartmentState(ApartmentState.STA);
            myGameThread.Start();

            FlyingText.NewFlyingText(this.screenRect.Width / 30, new System.Windows.Point(this.screenRect.Width / 2, this.screenRect.Height / 2), "Shapes!");
        }

        private void WindowClosing(object sender, CancelEventArgs e)
        {
            this.runningGameThread = false;
            Properties.Settings.Default.PrevWinPosition = this.RestoreBounds;
            Properties.Settings.Default.WindowState = (int)this.WindowState;
            Properties.Settings.Default.Save();
        }

        private void WindowClosed(object sender, EventArgs e)
        {
            SensorChooser.Kinect = null;
        }

        #endregion ctor + Window Events

        #region Kinect discovery + setup

        private void SensorChooserKinectSensorChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue != null)
            {
                this.UninitializeKinectServices((KinectSensor)e.OldValue);
            }


            if (e.NewValue != null)
            {
                this.InitializeKinectServices((KinectSensor)e.NewValue);
            }
        }

        // Kinect enabled apps should customize which Kinect services it initializes here.
        private KinectSensor InitializeKinectServices(KinectSensor sensor)
        {
            // Application should enable all streams first.
            sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);


            sensor.ColorFrameReady += this.ColorImageReady;


            sensor.SkeletonFrameReady += this.SkeletonsReady;
            sensor.SkeletonStream.Enable(new TransformSmoothParameters()
                                             {
                                                 Smoothing = 0.5f,
                                                 Correction = 0.5f,
                                                 Prediction = 0.5f,
                                                 JitterRadius = 0.05f,
                                                 MaxDeviationRadius = 0.04f
                                             });

            try
            {
                sensor.Start();
            }
            catch (IOException)
            {
                SensorChooser.AppConflictOccurred();
                return null;
            }


            


            return sensor;
        }

        // Kinect enabled apps should uninitialize all Kinect services that were initialized in InitializeKinectServices() here.
        private void UninitializeKinectServices(KinectSensor sensor)
        {
            sensor.Stop();

            sensor.SkeletonFrameReady -= this.SkeletonsReady;

        }

        #endregion Kinect discovery + setup

        #region Kinect Skeleton processing
        private void SkeletonsReady(object sender, SkeletonFrameReadyEventArgs e)
        {
            using (SkeletonFrame skeletonFrame = e.OpenSkeletonFrame())
            {
                if (skeletonFrame != null)
                {
                    int skeletonSlot = 0;

                    if ((this.skeletonData == null) || (this.skeletonData.Length != skeletonFrame.SkeletonArrayLength))
                    {
                        this.skeletonData = new Skeleton[skeletonFrame.SkeletonArrayLength];
                    }

                    skeletonFrame.CopySkeletonDataTo(this.skeletonData);

                    foreach (Skeleton skeleton in this.skeletonData)
                    {
                        if (SkeletonTrackingState.Tracked == skeleton.TrackingState)
                        {
                            Player player;
                            if (this.players.ContainsKey(skeletonSlot))
                            {
                                player = this.players[skeletonSlot];
                            }
                            else
                            {
                                player = new Player(skeletonSlot);
                                player.SetBounds(this.playerBounds);
                                this.players.Add(skeletonSlot, player);
                            }

                            player.LastUpdated = DateTime.Now;

                            // Update player's bone and joint positions
                            if (skeleton.Joints.Count > 0)
                            {
                                player.IsAlive = true;

                                // Head, hands, feet (hit testing happens in order here)
                                player.UpdateJointPosition(skeleton.Joints, JointType.Head);
                                player.UpdateJointPosition(skeleton.Joints, JointType.HandLeft);
                                player.UpdateJointPosition(skeleton.Joints, JointType.HandRight);
                                player.UpdateJointPosition(skeleton.Joints, JointType.FootLeft);
                                player.UpdateJointPosition(skeleton.Joints, JointType.FootRight);

                                // Hands and arms
                                player.UpdateBonePosition(skeleton.Joints, JointType.HandRight, JointType.WristRight);
                                player.UpdateBonePosition(skeleton.Joints, JointType.WristRight, JointType.ElbowRight);
                                player.UpdateBonePosition(skeleton.Joints, JointType.ElbowRight, JointType.ShoulderRight);

                                player.UpdateBonePosition(skeleton.Joints, JointType.HandLeft, JointType.WristLeft);
                                player.UpdateBonePosition(skeleton.Joints, JointType.WristLeft, JointType.ElbowLeft);
                                player.UpdateBonePosition(skeleton.Joints, JointType.ElbowLeft, JointType.ShoulderLeft);

                                // Head and Shoulders
                                player.UpdateBonePosition(skeleton.Joints, JointType.ShoulderCenter, JointType.Head);
                                player.UpdateBonePosition(skeleton.Joints, JointType.ShoulderLeft, JointType.ShoulderCenter);
                                player.UpdateBonePosition(skeleton.Joints, JointType.ShoulderCenter, JointType.ShoulderRight);

                                // Legs
                                player.UpdateBonePosition(skeleton.Joints, JointType.HipLeft, JointType.KneeLeft);
                                player.UpdateBonePosition(skeleton.Joints, JointType.KneeLeft, JointType.AnkleLeft);
                                player.UpdateBonePosition(skeleton.Joints, JointType.AnkleLeft, JointType.FootLeft);

                                player.UpdateBonePosition(skeleton.Joints, JointType.HipRight, JointType.KneeRight);
                                player.UpdateBonePosition(skeleton.Joints, JointType.KneeRight, JointType.AnkleRight);
                                player.UpdateBonePosition(skeleton.Joints, JointType.AnkleRight, JointType.FootRight);

                                player.UpdateBonePosition(skeleton.Joints, JointType.HipLeft, JointType.HipCenter);
                                player.UpdateBonePosition(skeleton.Joints, JointType.HipCenter, JointType.HipRight);

                                // Spine
                                player.UpdateBonePosition(skeleton.Joints, JointType.HipCenter, JointType.ShoulderCenter);

                            }
                        }

                        skeletonSlot++;
                    }
                }
            }
        }

        private void CheckPlayers()
        {
            foreach (var player in this.players)
            {
                if (!player.Value.IsAlive)
                {
                    // Player left scene since we aren't tracking it anymore, so remove from dictionary
                    this.players.Remove(player.Value.GetId());
                    break;
                }
            }

            // Count alive players
            int alive = this.players.Count(player => player.Value.IsAlive);

            if (alive != this.playersAlive)
            {
                if (alive == 2)
                {
                    this.myFallingThings.SetGameMode(GameMode.TwoPlayer);
                }
                else if (alive == 1)
                {
                    this.myFallingThings.SetGameMode(GameMode.Solo);
                }
                else if (alive == 0)
                {
                    this.myFallingThings.SetGameMode(GameMode.Off);
                }

                if ((this.playersAlive == 0) )
                {
                    BannerText.NewBanner(
                        Properties.Resources.Vocabulary,
                        this.screenRect,
                        true,
                        System.Windows.Media.Color.FromArgb(200, 255, 255, 255));
                }

                this.playersAlive = alive;
            }
        }

        private void PlayfieldSizeChanged(object sender, SizeChangedEventArgs e)
        {
            this.UpdatePlayfieldSize();
        }

        private void UpdatePlayfieldSize()
        {
            // Size of player wrt size of playfield, putting ourselves low on the screen.
            this.screenRect.X = 0;
            this.screenRect.Y = 0;
            this.screenRect.Width = this.playfield.ActualWidth;
            this.screenRect.Height = this.playfield.ActualHeight;

            BannerText.UpdateBounds(this.screenRect);

            this.playerBounds.X = 0;
            this.playerBounds.Width = this.playfield.ActualWidth;
            this.playerBounds.Y = this.playfield.ActualHeight * 0.2;
            this.playerBounds.Height = this.playfield.ActualHeight * 0.75;

            foreach (var player in this.players)
            {
                player.Value.SetBounds(this.playerBounds);
            }

            Rect fallingBounds = this.playerBounds;
            fallingBounds.Y = 0;
            fallingBounds.Height = playfield.ActualHeight;
            if (this.myFallingThings != null)
            {
                this.myFallingThings.SetBoundaries(fallingBounds);
            }
        }
        #endregion Kinect Skeleton processing

        #region GameTimer/Thread

        bool sceneTrigger = false;
        enum GameScene {
            GAME_SCENE_NONE,
            GAME_SCENE_INTRO_0_0_TITLE,
            GAME_SCENE_INTRO_0_1_RULE,
            GAME_SCENE_STAGE_1_0_STAGE_LEFT,
            GAME_SCENE_STAGE_1_1_CALIBRATE,
            GAME_SCENE_STAGE_1_2_MAP_LOAD,
            GAME_SCENE_STAGE_1_3_GAME_PLAY,
            GAME_SCENE_STAGE_1_4_RESULT,
            GAME_SCENE_STAGE_2_0_STAGE_LEFT,
            GAME_SCENE_STAGE_2_1_CALIBRATE,
            GAME_SCENE_STAGE_2_2_MAP_LOAD,
            GAME_SCENE_STAGE_2_3_GAME_PLAY,
            GAME_SCENE_STAGE_2_4_RESULT,
            GAME_SCENE_STAGE_3_0_STAGE_LEFT,
            GAME_SCENE_STAGE_3_1_CALIBRATE,
            GAME_SCENE_STAGE_3_2_MAP_LOAD,
            GAME_SCENE_STAGE_3_3_GAME_PLAY,
            GAME_SCENE_STAGE_3_4_RESULT,
            GAME_SCENE_STAGE_4_0_STAGE_LEFT,
            GAME_SCENE_STAGE_4_1_CALIBRATE,
            GAME_SCENE_STAGE_4_2_MAP_LOAD,
            GAME_SCENE_STAGE_4_3_GAME_PLAY,
            GAME_SCENE_STAGE_4_4_RESULT,
            GAME_SCENE_RESULT_5_0_GOAL,
            GAME_SCENE_RESULT_5_1_TITLE,
            GAME_SCENE_RESULT_5_2_MOVIE,
            GAME_SCENE_RESULT_5_3_END,
            GAME_SCENE_MAX 
        };

        private GameScene gameScene = GameScene.GAME_SCENE_NONE;

        private void GameThread()
        {
            this.runningGameThread = true;
            this.predNextFrame = DateTime.Now;
            this.actualFrameTime = 1000.0 / this.targetFramerate;

            this.gameScene = GameScene.GAME_SCENE_INTRO_0_0_TITLE;
            timerOn = true;
            timerTick = 0;
            timerMax = 40;
            sceneTrigger = false;
            openingSound.Play();

            myBoundingBoxes.SetBounds(this.playerBounds);
            //myBoundingBoxes.AddBox(200, 250, 300, 350);
            //myBoundingBoxes.AddBox(400, 250, 500, 300);


            // Try to dispatch at as constant of a framerate as possible by sleeping just enough since
            // the last time we dispatched.
            while (this.runningGameThread)
            {
                SceneCheck();
                Console.WriteLine("Num:" + this.myFallingThings.GetThingsNum());


                // Calculate average framerate.  
                DateTime now = DateTime.Now;
                if (this.lastFrameDrawn == DateTime.MinValue)
                {
                    this.lastFrameDrawn = now;
                }

                double ms = now.Subtract(this.lastFrameDrawn).TotalMilliseconds;
                this.actualFrameTime = (this.actualFrameTime * 0.95) + (0.05 * ms);
                this.lastFrameDrawn = now;

                // Adjust target framerate down if we're not achieving that rate
                this.frameCount++;
                if ((this.frameCount % 100 == 0) && (1000.0 / this.actualFrameTime < this.targetFramerate * 0.92))
                {
                    this.targetFramerate = Math.Max(MinFramerate, (this.targetFramerate + (1000.0 / this.actualFrameTime)) / 2);
                }

                if (now > this.predNextFrame)
                {
                    this.predNextFrame = now;
                }
                else
                {
                    double milliseconds = this.predNextFrame.Subtract(now).TotalMilliseconds;
                    if (milliseconds >= TimerResolution)
                    {
                        Thread.Sleep((int)(milliseconds + 0.5));
                    }
                }

                this.predNextFrame += TimeSpan.FromMilliseconds(1000.0 / this.targetFramerate);

                if (null != this.skeletonData && 0 < this.skeletonData.Length)
                {
                    foreach (Skeleton skeleton in this.skeletonData)
                    {
                        try
                        {
                            if (SkeletonTrackingState.Tracked == skeleton.TrackingState)
                            {
                                //Joint joint = skeleton.Joints[JointType.HandLeft];
                                myBoundingBoxes.IsBounced(skeleton);
                                //Console.WriteLine("1:" + joint.Position.X);
                            }
                        }
                        catch (Exception)
                        {
                        }
                        
                    }
                }
                 
                /*
                foreach (int skeletonSlot in this.players.Keys)
                {
                    if (this.players.ContainsKey(skeletonSlot))
                    {
                        Player player = this.players[skeletonSlot];
                        Dictionary<Bone, BoneData> segments = player.Segments;
                        DateTime cur = DateTime.Now;
                        try
                        {


                            foreach (var segment in segments)
                            {
                                Segment seg = segment.Value.GetEstimatedSegment(cur);
                                if (seg.IsCircle())
                                {
                                    Console.WriteLine("1:" + seg.X1);
                                }
                                skeleton.Joints[seg];
                                
                            }


                        }
                        catch
                        {

                        }
                        
                    }
                }
                */
                

                this.Dispatcher.Invoke(DispatcherPriority.Send, new Action<int>(this.HandleGameTimer), 0);
            }
        }

        private void HandleGameTimer(int param)
        {

            // Every so often, notify what our actual framerate is
            if ((this.frameCount % 100) == 0)
            {
                this.myFallingThings.SetFramerate(1000.0 / this.actualFrameTime);
            }

            // Advance animations, and do hit testing.
            for (int i = 0; i < NumIntraFrames; ++i)
            {
                foreach (var pair in this.players)
                {
                    HitType hit = this.myFallingThings.LookForHits(pair.Value.Segments, pair.Value.GetId());
                    if ((hit & HitType.Squeezed) != 0)
                    {
                        //this.squeezeSound.Play();
                    }
                    else if ((hit & HitType.Popped) != 0)
                    {
                        //this.popSound.Play();
                    }
                    else if ((hit & HitType.Hand) != 0)
                    {
                        //this.hitSound.Play();
                    }
                }

                this.myFallingThings.AdvanceFrame();

            }

            // Draw new Wpf scene by adding all objects to canvas
            playfield.Children.Clear();
            this.myFallingThings.DrawFrame(this.playfield.Children);
            foreach (var player in this.players)
            {
                player.Value.Draw(playfield.Children);
            }

            ImageChanger();
            myBoundingBoxes.DrawBoxes(playfield.Children);
            

            BannerText.Draw(playfield.Children);
            FlyingText.Draw(playfield.Children);

            this.CheckPlayers();
        }


        private ResourceManager rm = new ResourceManager("ShapeGame.Properties.Resources", System.Reflection.Assembly.GetExecutingAssembly());
        private BitmapImage IMAGE_NONE = new BitmapImage();
        private BitmapImage IMAGE_INTRO_0_0_TITLE = new BitmapImage();
        private BitmapImage IMAGE_INTRO_0_1_RULE = new BitmapImage();
        private BitmapImage IMAGE_STAGE_1_0_STAGE_LEFT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_1_1_CALIBRATE = new BitmapImage();
        private BitmapImage IMAGE_STAGE_1_4_RESULT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_2_0_STAGE_LEFT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_2_1_CALIBRATE = new BitmapImage();
        private BitmapImage IMAGE_STAGE_2_4_RESULT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_3_0_STAGE_LEFT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_3_1_CALIBRATE = new BitmapImage();
        private BitmapImage IMAGE_STAGE_3_4_RESULT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_4_0_STAGE_LEFT = new BitmapImage();
        private BitmapImage IMAGE_STAGE_4_1_CALIBRATE = new BitmapImage();
        private BitmapImage IMAGE_STAGE_4_4_RESULT = new BitmapImage();
        private BitmapImage IMAGE_RESULT_5_0_GOAL = new BitmapImage();
        private BitmapImage IMAGE_RESULT_5_1_TITLE = new BitmapImage();
        private BitmapImage IMAGE_RESULT_5_3_END = new BitmapImage();

        private void ImageLoader()
        {
            ImageLoad(IMAGE_NONE, "image_dummy");
            ImageLoad(IMAGE_INTRO_0_0_TITLE, "intro_Title");
            ImageLoad(IMAGE_INTRO_0_1_RULE, "intro_Info");
            ImageLoad(IMAGE_STAGE_1_0_STAGE_LEFT, "stage_start_4left");
            ImageLoad(IMAGE_STAGE_1_1_CALIBRATE, "calibrate");
            ImageLoad(IMAGE_STAGE_1_4_RESULT, "success_image");
            ImageLoad(IMAGE_STAGE_2_0_STAGE_LEFT, "stage_start_3left");
            ImageLoad(IMAGE_STAGE_2_1_CALIBRATE, "calibrate");
            ImageLoad(IMAGE_STAGE_2_4_RESULT, "success_image");
            ImageLoad(IMAGE_STAGE_3_0_STAGE_LEFT, "stage_start_2left");
            ImageLoad(IMAGE_STAGE_3_1_CALIBRATE, "calibrate");
            ImageLoad(IMAGE_STAGE_3_4_RESULT, "success_image");
            ImageLoad(IMAGE_STAGE_4_0_STAGE_LEFT, "stage_start_1left");
            ImageLoad(IMAGE_STAGE_4_1_CALIBRATE, "calibrate");
            ImageLoad(IMAGE_STAGE_4_4_RESULT, "success_image");
            ImageLoad(IMAGE_RESULT_5_0_GOAL, "goal_image");
            ImageLoad(IMAGE_RESULT_5_1_TITLE, "result_title");
            ImageLoad(IMAGE_RESULT_5_3_END, "result_end");

        }
        private void ImageLoad(BitmapImage bitmapImage, String name)
        {


            Bitmap bitmapimg = (Bitmap)rm.GetObject(name); //예를들어 Myimg.png를 읽어 리소스 매니저에서Myimg1이 되었다면 Myimg1을 적어야 한다.

            System.Drawing.Image img = (System.Drawing.Image)bitmapimg;
            MemoryStream ms = new MemoryStream();
            img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

            bitmapImage.BeginInit();
            bitmapImage.StreamSource = ms;
            bitmapImage.EndInit();
        }

        bool tryCaptureOnce = true;

        private void ImageChanger()
        {
            switch (this.gameScene)
            {
                case GameScene.GAME_SCENE_INTRO_0_0_TITLE:
                    UI_Layer1.Source = IMAGE_INTRO_0_0_TITLE;
                    break;
                case GameScene.GAME_SCENE_INTRO_0_1_RULE:
                    UI_Layer1.Source = IMAGE_INTRO_0_1_RULE;
                    break;
                case GameScene.GAME_SCENE_STAGE_1_0_STAGE_LEFT:
                    UI_Layer1.Source = IMAGE_STAGE_1_0_STAGE_LEFT;
                    break;
                case GameScene.GAME_SCENE_STAGE_1_1_CALIBRATE:
                    UI_Layer1.Source = IMAGE_STAGE_1_1_CALIBRATE;
                    break;
                case GameScene.GAME_SCENE_STAGE_1_2_MAP_LOAD:
                    UI_Layer1.Source = IMAGE_NONE;
                    if (tryCaptureOnce)
                    {
                        ImageSave("Snap_000_000");
                        tryCaptureOnce = false;
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_1_3_GAME_PLAY:
                    break;
                case GameScene.GAME_SCENE_STAGE_1_4_RESULT:
                    UI_Layer1.Source = IMAGE_STAGE_1_4_RESULT;
                    break;
                case GameScene.GAME_SCENE_STAGE_2_1_CALIBRATE:
                    UI_Layer1.Source = IMAGE_STAGE_2_1_CALIBRATE;
                    break;
                case GameScene.GAME_SCENE_STAGE_2_2_MAP_LOAD:
                    UI_Layer1.Source = IMAGE_NONE;
                    break;
                case GameScene.GAME_SCENE_STAGE_2_3_GAME_PLAY:
                    break;
                case GameScene.GAME_SCENE_STAGE_2_4_RESULT:
                    UI_Layer1.Source = IMAGE_STAGE_2_4_RESULT;
                    break;
                case GameScene.GAME_SCENE_STAGE_3_1_CALIBRATE:
                    UI_Layer1.Source = IMAGE_STAGE_3_1_CALIBRATE;
                    break;
                case GameScene.GAME_SCENE_STAGE_3_2_MAP_LOAD:
                    UI_Layer1.Source = IMAGE_NONE;
                    break;
                case GameScene.GAME_SCENE_STAGE_3_3_GAME_PLAY:
                    break;
                case GameScene.GAME_SCENE_STAGE_3_4_RESULT:
                    UI_Layer1.Source = IMAGE_STAGE_4_4_RESULT;
                    break;
                case GameScene.GAME_SCENE_STAGE_4_1_CALIBRATE:
                    UI_Layer1.Source = IMAGE_STAGE_4_1_CALIBRATE;
                    break;
                case GameScene.GAME_SCENE_STAGE_4_2_MAP_LOAD:
                    UI_Layer1.Source = IMAGE_NONE;
                    break;
                case GameScene.GAME_SCENE_STAGE_4_3_GAME_PLAY:
                    break;
                case GameScene.GAME_SCENE_RESULT_5_0_GOAL:
                    UI_Layer1.Source = IMAGE_RESULT_5_0_GOAL;
                    break;
                case GameScene.GAME_SCENE_RESULT_5_1_TITLE:
                    UI_Layer1.Source = IMAGE_RESULT_5_1_TITLE;
                    break;

            }
        }

        private void StopAllSound()
        {
            openingSound.Stop();
            calibrateSound.Stop();
            stage1Sound.Stop();
            stage2Sound.Stop();
            stage3Sound.Stop();
            stage4Sound.Stop();
            successSound.Stop();
            goalSound.Stop();
            resultSound.Stop();
        }

        private void SceneCheck()
        {
            switch (this.gameScene)
            {
                case GameScene.GAME_SCENE_INTRO_0_0_TITLE:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_INTRO_0_1_RULE;
                        timerTick = 0;
                        timerMax = 30;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;
                case GameScene.GAME_SCENE_INTRO_0_1_RULE:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_1_0_STAGE_LEFT;
                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                        StopAllSound();
                        calibrateSound.Play();
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_1_0_STAGE_LEFT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_1_1_CALIBRATE;
                        timerTick = 0;
                        timerMax = 10;
                        timerOn = true;
                        sceneTrigger = false;

                        myBoundingBoxes.AddBox(200, 250, 300, 350);
                        myBoundingBoxes.AddBox(400, 250, 500, 300);
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_1_1_CALIBRATE:
                    if( myBoundingBoxes.CheckBounced() )
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_1_2_MAP_LOAD;
                        myBoundingBoxes.ResetBox();
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_1_2_MAP_LOAD:
                    this.gameScene = GameScene.GAME_SCENE_STAGE_1_3_GAME_PLAY;
                    this.myFallingThings.AddNewThing(150, 200);
                    this.myFallingThings.AddNewThing(450, 200);
                    StopAllSound();
                    stage1Sound.Play();
                    break;
                case GameScene.GAME_SCENE_STAGE_1_3_GAME_PLAY:
                    if (0 == this.myFallingThings.GetThingsNum())
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_1_4_RESULT;
                        StopAllSound();
                        successSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_1_4_RESULT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_2_0_STAGE_LEFT;
                        StopAllSound();
                        calibrateSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;





                case GameScene.GAME_SCENE_STAGE_2_0_STAGE_LEFT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_2_1_CALIBRATE;
                        timerTick = 0;
                        timerMax = 10;
                        timerOn = true;
                        sceneTrigger = false;

                        myBoundingBoxes.AddBox(200, 250, 300, 350);
                        myBoundingBoxes.AddBox(400, 250, 500, 300);
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_2_1_CALIBRATE:
                    if (myBoundingBoxes.CheckBounced())
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_2_2_MAP_LOAD;
                        myBoundingBoxes.ResetBox();
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_2_2_MAP_LOAD:
                    this.gameScene = GameScene.GAME_SCENE_STAGE_2_3_GAME_PLAY;
                    this.myFallingThings.AddNewThing(150, 450);
                    this.myFallingThings.AddNewThing(450, 450);
                    StopAllSound();
                    stage2Sound.Play();
                    break;
                case GameScene.GAME_SCENE_STAGE_2_3_GAME_PLAY:
                    if (0 == this.myFallingThings.GetThingsNum())
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_2_4_RESULT;
                        StopAllSound();
                        successSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_2_4_RESULT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_3_0_STAGE_LEFT;
                        StopAllSound();
                        calibrateSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;





                case GameScene.GAME_SCENE_STAGE_3_0_STAGE_LEFT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_3_1_CALIBRATE;
                        timerTick = 0;
                        timerMax = 10;
                        timerOn = true;
                        sceneTrigger = false;

                        myBoundingBoxes.AddBox(200, 250, 300, 350);
                        myBoundingBoxes.AddBox(400, 250, 500, 300);
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_3_1_CALIBRATE:
                    if (myBoundingBoxes.CheckBounced())
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_3_2_MAP_LOAD;
                        myBoundingBoxes.ResetBox();
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_3_2_MAP_LOAD:
                    this.gameScene = GameScene.GAME_SCENE_STAGE_3_3_GAME_PLAY;
                    this.myFallingThings.AddNewThing(150, 300);
                    this.myFallingThings.AddNewThing(450, 300);
                    StopAllSound();
                    stage3Sound.Play();
                    break;
                case GameScene.GAME_SCENE_STAGE_3_3_GAME_PLAY:
                    if (0 == this.myFallingThings.GetThingsNum())
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_3_4_RESULT;
                        StopAllSound();
                        successSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_3_4_RESULT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_4_0_STAGE_LEFT;
                        StopAllSound();
                        calibrateSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;










                case GameScene.GAME_SCENE_STAGE_4_0_STAGE_LEFT:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_4_1_CALIBRATE;
                        timerTick = 0;
                        timerMax = 10;
                        timerOn = true;
                        sceneTrigger = false;

                        myBoundingBoxes.AddBox(200, 250, 300, 350);
                        myBoundingBoxes.AddBox(400, 250, 500, 300);
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_4_1_CALIBRATE:
                    if (myBoundingBoxes.CheckBounced())
                    {
                        this.gameScene = GameScene.GAME_SCENE_STAGE_4_2_MAP_LOAD;
                        myBoundingBoxes.ResetBox();
                    }
                    break;
                case GameScene.GAME_SCENE_STAGE_4_2_MAP_LOAD:
                    this.gameScene = GameScene.GAME_SCENE_STAGE_4_3_GAME_PLAY;
                    this.myFallingThings.AddNewThing(300, 150);
                    this.myFallingThings.AddNewThing(200, 500);
                    this.myFallingThings.AddNewThing(400, 500);
                    StopAllSound();
                    stage4Sound.Play();
                    break;
                case GameScene.GAME_SCENE_STAGE_4_3_GAME_PLAY:
                    if (0 == this.myFallingThings.GetThingsNum())
                    {
                        this.gameScene = GameScene.GAME_SCENE_RESULT_5_0_GOAL;
                        StopAllSound();
                        goalSound.Play();

                        timerTick = 0;
                        timerMax = 25;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;
                case GameScene.GAME_SCENE_RESULT_5_0_GOAL:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_RESULT_5_1_TITLE;
                        StopAllSound();
                        resultSound.Play();

                        timerTick = 0;
                        timerMax = 15;
                        timerOn = true;
                        sceneTrigger = false;
                    }
                    break;


            }

        }


        private bool timerOn = false;
        private uint timerTick = 0;
        private uint timerMax = 0;
        private void dispatcherTimer_Tick(object sender, EventArgs e)
        {
            if (timerOn)
            {
                ++timerTick;

                if (timerTick > timerMax)
                {
                    sceneTrigger = true;
                    timerOn = false;
                    timerTick = 0;
                }
            }
        }

        #endregion GameTimer/Thread



        #region imageSave
        public static WriteableBitmap outputImage;
        public static bool IsPhotoReady = false;



        private ColorImageFormat lastImageFormat = ColorImageFormat.Undefined;
        private static readonly int Bgr32BytesPerPixel = (PixelFormats.Bgr32.BitsPerPixel + 7) / 8;

        private void ColorImageReady(object sender, ColorImageFrameReadyEventArgs e)
        {
            using (ColorImageFrame imageFrame = e.OpenColorImageFrame())
            {
                if (imageFrame != null)
                {

                    //bool haveNewFormat = this.lastImageFormat != imageFrame.Format;




                    byte[] pixelData = new byte[imageFrame.PixelDataLength];

                    imageFrame.CopyPixelDataTo(pixelData);

                    //if (IsPhotoReady)
                    //{
                        outputImage = new WriteableBitmap(
                                imageFrame.Width,
                                imageFrame.Height,
                                96,  // DpiX
                                96,  // DpiY
                                PixelFormats.Bgr32,
                                null);

                        outputImage.WritePixels(
                        new Int32Rect(0, 0, imageFrame.Width, imageFrame.Height),
                        pixelData,
                        imageFrame.Width * Bgr32BytesPerPixel, 0);

                        IsPhotoReady = false;

                    //}
                    lastImageFormat = imageFrame.Format;


                }
            }
        }

        public static void ImageSave(String fileName)
        {
            
            using (FileStream stream5 = new FileStream(fileName+".png", FileMode.Create))
            {
                PngBitmapEncoder encoder5 = new PngBitmapEncoder();
                encoder5.Frames.Add(BitmapFrame.Create(outputImage));
                encoder5.Save(stream5);
                stream5.Close();
            }
        }



        private void ImageLoad()
        {
            /*
            Snap_150_200.png
            Snap_150_300.png
            Snap_150_450.png
            Snap_200_500.png
            Snap_300_150.png
            Snap_400_500.png
            Snap_450_200.png
            Snap_450_300.png
            Snap_450_450.png
            */


        }
        #endregion imageSave


    }
}
