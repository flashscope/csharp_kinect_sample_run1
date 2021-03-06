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

            FlyingText.NewFlyingText(this.screenRect.Width / 30, new System.Windows.Point(this.screenRect.Width / 2, this.screenRect.Height / 2), " ");


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
        private bool shutDownAPP = false;
        private void GameThread()
        {
            this.runningGameThread = true;
            this.predNextFrame = DateTime.Now;
            this.actualFrameTime = 1000.0 / this.targetFramerate;
            
            this.gameScene = GameScene.GAME_SCENE_INTRO_0_0_TITLE;
            //this.gameScene = GameScene.GAME_SCENE_STAGE_4_0_STAGE_LEFT;

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
                if (shutDownAPP)
                {
                    break;
                }
                
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
        bool tryCaptureLoadOnce = true;
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

                    if (tryCaptureLoadOnce)
                    {
                        CapturePhotoLoad();
                        tryCaptureLoadOnce = false;
                    }

                    break;
                case GameScene.GAME_SCENE_RESULT_5_2_MOVIE:
                    ResultAnimationShow();
                    break;
                case GameScene.GAME_SCENE_RESULT_5_3_END:
                    UI_Layer1.Source = IMAGE_RESULT_5_3_END;
                    break;
                    
            }
        }
        
        private void ResultAnimationShow()
        {
            if (resultAnimationOn)
            {
                switch (timerTick)
                {
                    case 27: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 28: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 29: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 30: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 31: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 32: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 33: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 34: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 35: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 36: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 37: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 38: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 39: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 40: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 41: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 42: UI_Layer1.Source = SNAP_PHOTO_RIGHT_3; break;
                    case 43: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 44: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 45: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 46: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 47: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 48: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 49: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 50: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 51: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 52: UI_Layer1.Source = SNAP_PHOTO_RIGHT_3; break;
                    case 53: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 54: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 55: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 56: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 57: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 58: UI_Layer1.Source = SNAP_PHOTO_RIGHT_3; break;
                    case 59: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 60: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 61: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 62: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 63: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 64: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 65: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 66: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 67: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 68: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 69: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 70: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 71: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 72: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 73: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 74: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 75: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 76: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 77: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 78: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 79: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 80: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 81: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 82: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 83: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 84: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 85: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 86: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 87: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 88: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 89: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 90: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 91: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 92: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 93: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 94: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 95: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 96: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 97: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 98: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 99: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 100: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 101: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 102: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 103: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 104: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 105: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 106: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 107: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 108: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 109: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 110: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 111: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 112: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 113: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 114: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 115: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 116: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 117: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 118: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 119: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 120: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 121: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 122: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 123: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 124: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 125: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 126: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 127: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 128: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 129: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 130: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 131: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 132: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 133: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 134: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 135: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 136: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 137: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 138: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 139: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 140: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 141: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 142: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 143: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 144: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 145: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 146: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 147: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 148: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 149: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 150: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 151: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 152: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 153: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 154: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 155: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 156: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 157: UI_Layer1.Source = SNAP_PHOTO_RIGHT_3; break;
                    case 158: UI_Layer1.Source = SNAP_PHOTO_RIGHT_1; break;
                    case 159: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 160: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 161: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 162: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 163: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 164: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 165: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 166: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 167: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 168: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 169: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 170: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 171: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 172: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 173: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 174: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 175: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 176: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 177: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 178: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 179: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 180: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 181: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 182: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 183: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 184: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 185: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 186: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 187: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 188: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 189: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 190: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 191: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 192: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 193: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 194: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 195: UI_Layer1.Source = SNAP_PHOTO_RIGHT_3; break;
                    case 196: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 197: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 198: UI_Layer1.Source = SNAP_PHOTO_RIGHT_4; break;
                    case 199: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 200: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 201: UI_Layer1.Source = SNAP_PHOTO_LEFT_4; break;
                    case 202: UI_Layer1.Source = SNAP_PHOTO_LEFT_2; break;
                    case 203: UI_Layer1.Source = SNAP_PHOTO_LEFT_3; break;
                    case 204: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 205: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;
                    case 206: UI_Layer1.Source = SNAP_PHOTO_TOP; break;
                    case 207: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 208: UI_Layer1.Source = SNAP_PHOTO_LEFT_1; break;
                    case 209: UI_Layer1.Source = SNAP_PHOTO_DEFAULT; break;
                    case 210: UI_Layer1.Source = SNAP_PHOTO_RIGHT_2; break;





                }
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
                case GameScene.GAME_SCENE_RESULT_5_1_TITLE:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_RESULT_5_2_MOVIE;

                        timerTick = 0;
                        timerMax = 210;
                        timerOn = true;
                        sceneTrigger = false;
                        resultAnimationOn = true;
                    }
                    break;
                case GameScene.GAME_SCENE_RESULT_5_2_MOVIE:
                    if (sceneTrigger)
                    {
                        this.gameScene = GameScene.GAME_SCENE_RESULT_5_3_END;

                        timerTick = 0;
                        timerMax = 50;
                        timerOn = true;
                        sceneTrigger = false;
                        resultAnimationOn = false;

                        // delete files
                        DeleteCaptureFiles();
                    }
                    break;
                case GameScene.GAME_SCENE_RESULT_5_3_END:
                    if (sceneTrigger)
                    {
                        // program end
                        shutDownAPP = true;
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

            if (shutDownAPP)
            {
                Application.Current.Shutdown();
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

        private bool resultAnimationOn = false;
        private BitmapImage SNAP_PHOTO_DEFAULT = new BitmapImage();
        private BitmapImage SNAP_PHOTO_LEFT_1 = new BitmapImage();
        private BitmapImage SNAP_PHOTO_LEFT_2 = new BitmapImage();
        private BitmapImage SNAP_PHOTO_LEFT_3 = new BitmapImage();
        private BitmapImage SNAP_PHOTO_LEFT_4 = new BitmapImage();

        private BitmapImage SNAP_PHOTO_TOP = new BitmapImage();

        private BitmapImage SNAP_PHOTO_RIGHT_4 = new BitmapImage();
        private BitmapImage SNAP_PHOTO_RIGHT_1 = new BitmapImage();
        private BitmapImage SNAP_PHOTO_RIGHT_2 = new BitmapImage();
        private BitmapImage SNAP_PHOTO_RIGHT_3 = new BitmapImage();

        private void CapturePhotoLoad()
        {
            /*
            Snap_000_000.png
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
            String curDir = Directory.GetCurrentDirectory();
            curDir = curDir + "\\";

            SNAP_PHOTO_DEFAULT.BeginInit();
            SNAP_PHOTO_DEFAULT.UriSource = new Uri(curDir + "Snap_000_000.png");
            SNAP_PHOTO_DEFAULT.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_DEFAULT.EndInit();

            SNAP_PHOTO_LEFT_1.BeginInit();
            SNAP_PHOTO_LEFT_1.UriSource = new Uri(curDir + "Snap_150_200.png");
            SNAP_PHOTO_LEFT_1.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_LEFT_1.EndInit();

            SNAP_PHOTO_LEFT_2.BeginInit();
            SNAP_PHOTO_LEFT_2.UriSource = new Uri(curDir + "Snap_150_300.png");
            SNAP_PHOTO_LEFT_2.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_LEFT_2.EndInit();

            SNAP_PHOTO_LEFT_3.BeginInit();
            SNAP_PHOTO_LEFT_3.UriSource = new Uri(curDir + "Snap_150_450.png");
            SNAP_PHOTO_LEFT_3.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_LEFT_3.EndInit();

            SNAP_PHOTO_LEFT_4.BeginInit();
            SNAP_PHOTO_LEFT_4.UriSource = new Uri(curDir + "Snap_200_500.png");
            SNAP_PHOTO_LEFT_4.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_LEFT_4.EndInit();

            SNAP_PHOTO_TOP.BeginInit();
            SNAP_PHOTO_TOP.UriSource = new Uri(curDir + "Snap_300_150.png");
            SNAP_PHOTO_TOP.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_TOP.EndInit();

            SNAP_PHOTO_RIGHT_4.BeginInit();
            SNAP_PHOTO_RIGHT_4.UriSource = new Uri(curDir + "Snap_400_500.png");
            SNAP_PHOTO_RIGHT_4.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_RIGHT_4.EndInit();

            SNAP_PHOTO_RIGHT_1.BeginInit();
            SNAP_PHOTO_RIGHT_1.UriSource = new Uri(curDir + "Snap_450_200.png");
            SNAP_PHOTO_RIGHT_1.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_RIGHT_1.EndInit();

            SNAP_PHOTO_RIGHT_2.BeginInit();
            SNAP_PHOTO_RIGHT_2.UriSource = new Uri(curDir + "Snap_450_300.png");
            SNAP_PHOTO_RIGHT_2.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_RIGHT_2.EndInit();

            SNAP_PHOTO_RIGHT_3.BeginInit();
            SNAP_PHOTO_RIGHT_3.UriSource = new Uri(curDir + "Snap_450_450.png");
            SNAP_PHOTO_RIGHT_3.CacheOption = BitmapCacheOption.OnLoad;
            SNAP_PHOTO_RIGHT_3.EndInit();

        }



        private void DeleteCaptureFiles()
        {


            DeleteFile("Snap_000_000.png");
            DeleteFile("Snap_150_200.png");
            DeleteFile("Snap_150_300.png");
            DeleteFile("Snap_150_450.png");
            DeleteFile("Snap_200_500.png");
            DeleteFile("Snap_300_150.png");
            DeleteFile("Snap_400_500.png");
            DeleteFile("Snap_450_200.png");
            DeleteFile("Snap_450_300.png");
            DeleteFile("Snap_450_450.png");


        }

        private void DeleteFile(String name)
        {
            String curDir = Directory.GetCurrentDirectory();
            curDir = curDir + "\\";

            System.IO.FileInfo fi = new System.IO.FileInfo( curDir + name );
            try
            {
                fi.Delete();
            }
            catch (System.IO.IOException e)
            {
                Console.WriteLine(e.Message);
            }
        }
        #endregion imageSave


    }
}
