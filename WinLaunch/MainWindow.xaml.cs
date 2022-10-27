﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;


//BUGS
// folder items sometimes cut when added
// crash when opening and saving in the settings while background thread is active

// Plans
//- auto add known programs
//- gif image tutorials

//- auto check for updates interval - done
//- animation callbacks
//- double keytap activation
//- maybe lock single options?
//- named pages
//- add in drives
//- auto reactivate aero
//- dock support
//- icon caches
//- custom layouts
//- continues paging
//- live folders
//- integrated browser
//- bug

namespace WinLaunch
{
    /// <summary>
    /// Interaktionslogik für MainWindow.xaml
    /// </summary>
    partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        bool FirstLaunch = false;
        bool JustUpdated = false;

        #region Interop

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("user32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        public IntPtr GetWindowHandle()
        {
            IntPtr windowHandle = new WindowInteropHelper(this).Handle;
            return windowHandle;
        }
        #endregion Interop

        #region Properties

        public static Window WindowRef;

        public SpringboardManager SBM { get; set; }

        //Animations
        private AnimationHelper CanvasOpacityAnim = null;

        private AnimationHelper CanvasScaleAnim = null;

        private bool StartingItem = false;
        private bool FadingOut = false;

        //workarounds
        private WallpaperUtils WPWatch = null;

        #region fps

        private Stopwatch FPSWatch = null;
        private double lasttime = 0;
        private int framerun = 10;
        private int curframe = 0;

        #endregion fps

        #endregion Properties

        #region Init

        public static List<string> AddFiles = null;

        //private Stopwatch startupTime = new Stopwatch();

        BackupManager backupManager;
        public Theme theme { get; set; }
        public Settings settings { get; set; }

        public MainWindow()
        {
            //show eula
            Eula.ShowEULA();

            Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;

            //startupTime.Start();
            //setup backup manager
            string ItemPath = Path.GetDirectoryName(ItemCollection.CurrentItemsPath);
            string ItemBackupPath = Path.Combine(ItemPath, "ICBackup");
            backupManager = new BackupManager(ItemBackupPath, 20);

            if (ShortcutActivation.FindAndActivate())
            {
                //wake main instance of WinLaunch then exit
                Environment.Exit(-1);
            }

            //when autostarted path is screwed up (c:/windows/system32/)
            SetHomeDirectory();

            InitializeComponent();

            CanvasOpacityAnim = new AnimationHelper(0.0, 1.0);
            CanvasScaleAnim = new AnimationHelper(0.4, 1.0);

            FPSWatch = new Stopwatch();
            WPWatch = new WallpaperUtils();

            //hook up events
            this.Loaded += new RoutedEventHandler(MainWindow_Loaded);

            //setup window
            this.AllowDrop = true;
            this.Topmost = true;
            this.ShowInTaskbar = false;

            //load settings
            InitImageRessources();
            this.ShowActivated = true;

            //start window hidden
            this.WindowStyle = System.Windows.WindowStyle.None;
            this.Width = 0;
            this.Height = 0;

            //load settings and setup deskmode / no deskmode
            Settings.CurrentSettings = Settings.LoadSettings(Settings.CurrentSettingsPath);
            
            //load theme
            Theme.CurrentTheme = Theme.LoadTheme();

            if(Theme.CurrentTheme.Rows != -1)
            {
                //old style 
                Settings.CurrentSettings.Columns = Theme.CurrentTheme.Columns;
                Settings.CurrentSettings.Rows = Theme.CurrentTheme.Rows;
                Settings.CurrentSettings.FolderColumns = Theme.CurrentTheme.FolderColumns;
                Settings.CurrentSettings.FolderRows = Theme.CurrentTheme.FolderRows;

                Settings.SaveSettings(Settings.CurrentSettingsPath, Settings.CurrentSettings);

                Theme.CurrentTheme.Columns = -1;
                Theme.CurrentTheme.Rows = -1;
                Theme.CurrentTheme.FolderColumns = -1;
                Theme.CurrentTheme.FolderRows = -1;

                Theme.SaveTheme(Theme.CurrentTheme);
            }

            if (Settings.CurrentSettings.DeskMode)
            {
                //Disable for desk mode on all plattforms
                this.AllowsTransparency = false;
            }
            else
            {
                //enable if aero is in use and available
                if (Theme.CurrentTheme.UseAeroBlur && GlassUtils.IsBlurBehindAvailable())
                {
                    this.AllowsTransparency = true;
                }
            }
        }

        void Current_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            CrashReporter.Report(e.Exception);

            MessageBox.Show("WinLaunch just crashed!\nplease submit a bug report to winlaunch.official@gmail.com\nerror: " + e.Exception.Message);
            Environment.Exit(1);
        }

        private void InitImageRessources()
        {
            //Set non themeable Image Ressources
            SBItem.LoadingImage = new BitmapImage(new Uri("pack://application:,,,/WinLaunch;component/res/Loading.png"));

            if (SBItem.LoadingImage.CanFreeze)
                SBItem.LoadingImage.Freeze();

            SBItem.shadow = this.FindResource("shadow") as BitmapImage;

            if (SBItem.shadow.CanFreeze)
                SBItem.shadow.Freeze();

            SBItem.DropFolderImage = this.FindResource("drop") as BitmapImage;

            if (SBItem.DropFolderImage.CanFreeze)
                SBItem.DropFolderImage.Freeze();
        }

        private void InitSBM()
        {
            SBM = new SpringboardManager();

            SBM.Init(this, this.MainCanvas);
            SBM.ParentWindow = this;
        }

        private void BeginInitIC(Action continueWith)
        {
            try
            {
                //do in parallel?
                if(!SBM.IC.LoadFromXML(ItemCollection.CurrentItemsPath))
                {
                    //item loading failed
                    //backup procedure
                    List<BackupEntry> backups = backupManager.GetBackups();

                    //try loading all backups until one succeeds
                    bool success = false;
                    for (int i = 0; i < backups.Count; i++)
                    {
                        if(SBM.IC.LoadFromXML(backups[i].path))
                        {
                            success = true;
                            break;
                        }
                    }

                    if(!success)
                    {
                        //failed to load items or backups
                        CrashReporter.Report("Item loading failed, tried " + backups.Count + " backups");
                        MessageBox.Show("Item loading failed", "WinLaunch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else
                    {
                        //immediately save new items
                        PerformItemBackup();
                    }
                }

                SBM.UpdateIC();

                FPSWatch.Start();
            }
            catch (Exception ex)
            {
                CrashReporter.Report(ex);
                MessageBox.Show(ex.Message);
            }

            SBM.IC.LoadIconsInBackground(Dispatcher, continueWith);
        }

        private void AddArgumentFiles()
        {
            if (AddFiles != null)
            {
                foreach (string file in AddFiles)
                {
                    AddFile(file);
                }
            }
        }

        #endregion Init

        #region Utils

        private void SetHomeDirectory()
        {
            string path = Assembly.GetExecutingAssembly().Location;
            path = System.IO.Path.GetDirectoryName(path);
            System.IO.Directory.SetCurrentDirectory(path);
        }

        private void CleanMemory()
        {
            GC.Collect();
        }

        #endregion Utils

        #region Search
        public bool SearchActive = false;

        private void ActivateSearch()
        {
            if (SearchActive)
                return;

            SearchActive = true;
            
            tbSearch.CaretBrush = new SolidColorBrush(Colors.White);

            SBM.UnselectItem();
        }

        private void DeactivateSearch()
        {
            tbSearch.Clear();

            SearchActive = false;

            tbSearch.CaretBrush = new SolidColorBrush(Colors.Transparent);
            
            SBM.EndSearch();
        }

        #endregion

        #region Folder Title Renaming

        public bool FolderRenamingActive = false;

        private void ActivateFolderRenaming()
        {
            FolderRenamingActive = true;

            if(Theme.CurrentTheme.UseVectorFolder)
            {
                FolderTitle.Visibility = System.Windows.Visibility.Collapsed;
                FolderTitleShadow.Visibility = System.Windows.Visibility.Collapsed;

                FolderTitleEdit.Visibility = System.Windows.Visibility.Visible;

                //focus field
                Keyboard.Focus(FolderTitleEdit);

                FolderTitleEdit.Text = FolderTitle.Text;
            }
            else
            {
                FolderTitleNew.Visibility = System.Windows.Visibility.Collapsed;
                FolderTitleShadowNew.Visibility = System.Windows.Visibility.Collapsed;

                FolderTitleEditNew.Visibility = System.Windows.Visibility.Visible;

                //focus field
                Keyboard.Focus(FolderTitleEditNew);

                FolderTitleEditNew.Text = FolderTitleNew.Text;
            }
            
        }

        private void DeactivateFolderRenaming()
        {
            FolderRenamingActive = false;
            if (Theme.CurrentTheme.UseVectorFolder)
            {
                FolderTitle.Visibility = System.Windows.Visibility.Visible;
                FolderTitleShadow.Visibility = System.Windows.Visibility.Visible;

                FolderTitleEdit.Visibility = System.Windows.Visibility.Collapsed;

                //Set the edited text as new title
                FolderTitle.Text = ValidateFolderName(FolderTitleEdit.Text);
                SBM.ActiveFolder.Name = FolderTitle.Text;
            }
            else
            {
                FolderTitleNew.Visibility = System.Windows.Visibility.Visible;
                FolderTitleShadowNew.Visibility = System.Windows.Visibility.Visible;

                FolderTitleEditNew.Visibility = System.Windows.Visibility.Collapsed;

                //Set the edited text as new title
                FolderTitleNew.Text = ValidateFolderName(FolderTitleEditNew.Text);
                SBM.ActiveFolder.Name = FolderTitleNew.Text;
            }
        }

        private void FolderTitle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            ActivateFolderRenaming();
            e.Handled = true;
        }

        private string ValidateFolderName(string FolderName)
        {
            if (FolderName == "")
                FolderName = "NewFolder";

            return FolderName;
        }

        //Direct events sent by springboard manager
        public void FolderOpened()
        {
            if(Theme.CurrentTheme.UseVectorFolder)
            {
                //update Folder title UI text
                FolderTitle.Text = SBM.ActiveFolder.Name;
                FolderTitleEdit.Text = FolderTitle.Text;
            }
            else
            {
                //update Folder title UI text
                FolderTitleNew.Text = SBM.ActiveFolder.Name;
                FolderTitleEditNew.Text = FolderTitleNew.Text;
            }

            //fade page counters out
            PageCounter.BeginAnimation(StackPanel.OpacityProperty, new DoubleAnimation(0.0, new Duration(new TimeSpan(0, 0, 0, 0, 200))));
        }

        public void FolderClosed()
        {
            DeactivateFolderRenaming();

            strokepath.Visibility = Visibility.Collapsed;

            //fade page counters in
            PageCounter.BeginAnimation(StackPanel.OpacityProperty, new DoubleAnimation(1.0, new Duration(new TimeSpan(0, 0, 0, 0, 200))));
        }

        private void FolderTitleEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                DeactivateFolderRenaming();
            }
        }

        #endregion Folder Title Renaming

        private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (SBM.LockItems)
                e.Handled = true;

            if(Settings.CurrentSettings.TabletMode)
                e.Handled = true;
        }
    }
}