using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CampusNetGuard
{
    internal static class Program
    {
        private static Mutex singleInstanceMutex;

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
