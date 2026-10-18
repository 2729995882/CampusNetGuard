using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CampusNetGuard
{
    internal static class Program
    {
        private static Mutex singleInstanceMutex;
        private static bool exitLogged;

        private static void InstallExceptionHandlers()
        {
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.ApplicationExit += LogApplicationExit;
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Logger.Error("界面线程发生未处理异常: " + DescribeException(e == null ? null : e.Exception));
            try
            {
                MessageBox.Show("校园网助手遇到异常，详细信息已写入日志。程序将继续运行。",
                    "校园网助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Error("应用程序发生未处理异常: " +
                DescribeException(e == null ? null : e.ExceptionObject as Exception));
        }

        private static void LogApplicationExit(object sender, EventArgs e)
        {
            if (exitLogged)
                return;
            exitLogged = true;
            Logger.Info("校园网助手已退出。");
        }

        private static string DescribeException(Exception exception)
        {
            if (exception == null)
                return "未知异常";
            string message = exception.ToString();
            return message.Length > 2000 ? message.Substring(0, 2000) : message;
        }

        private static void CleanupPreviousVersion()
        {
            try
            {
                string oldExecutable = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "CampusNetGuard.old.exe");
                if (File.Exists(oldExecutable))
                    File.Delete(oldExecutable);
            }
            catch
            {
                // A running old version may still be locked. It will be removed on a later start.
            }
        }

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            singleInstanceMutex = new Mutex(false, @"Local\CampusNetGuard.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("校园网助手已经在运行，请查看系统托盘。",
                    "校园网助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                CleanupPreviousVersion();
                InstallExceptionHandlers();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GuardContext());
            }
            finally
            {
                if (singleInstanceMutex != null)
                {
                    try { singleInstanceMutex.ReleaseMutex(); } catch { }
                    try { singleInstanceMutex.Dispose(); } catch { }
                    singleInstanceMutex = null;
                }
            }
        }
    }
}
