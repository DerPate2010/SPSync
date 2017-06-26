using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Windows;
using System.Timers;
using System.Net;
using SPSync.Core;

namespace SPSync
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly MainViewModel viewModel = new MainViewModel();
        private Timer syncTimer;

        internal static MainViewModel MainViewModel
        {
            get
            {
                if (!viewModel.IsInitialized)
                    viewModel.Init();

                return viewModel;
            }
        }

        public App()
        {
            //SquirrelSetup.HandleStartup();

            Logger.Log(string.Join("|", Environment.GetCommandLineArgs()));
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;

            //HACK: disable SSL certificate check
            //ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };

            string[] args = Environment.GetCommandLineArgs();

            if (args.Length == 2)
            {
                if (args[1].EndsWith("spsync"))
                {
                    DownloadWindow dw = new DownloadWindow();
                    dw.ShowDialog();
                    return;
                }
            }

            base.OnStartup(e);

            //int intervalMinutes = 20;
            //if (!int.TryParse(ConfigurationManager.AppSettings["AutoSyncInterval"], out intervalMinutes))
            //    intervalMinutes = 20;
            
            //syncTimer = new Timer();
            //syncTimer.AutoReset = true;
            //syncTimer.Elapsed += new ElapsedEventHandler(syncTimer_Elapsed);
            //syncTimer.Interval = new TimeSpan(0, intervalMinutes, 0).TotalMilliseconds;
            //syncTimer.Start();
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogEx(e.ExceptionObject as Exception);
        }

        void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {

            LogEx(e.Exception);
        }

        private static void LogEx(Exception ex)
        {
            Logger.Log("Unhandled Exception: {0}", ex.ToString());

            var innerEx = ex.InnerException;
            while (innerEx != null)
            {
                Logger.Log("--->{0}", innerEx.Message);
                innerEx = innerEx.InnerException;
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
        }

        void syncTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            MainViewModel.SyncAll();
        }
    }
}
