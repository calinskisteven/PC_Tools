using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WorkTimeTaskbar
{
    internal static class Program
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

        [STAThread]
        static void Main()
        {
            bool acquired;
            using (var mutex = new Mutex(true, @"Local\WorkTimeTaskbar.SingleInstance", out acquired))
            {
                if (!acquired)
                {
                    MessageBox.Show("Work Timer is already running. Open the existing instance instead.",
                        "Work Timer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Keep the running taskbar icon separate from Windows' static EXE icon.
                string appUserModelId = "WorkTimeTaskbar.LiveTimer." + Process.GetCurrentProcess().Id;
                SetCurrentProcessExplicitAppUserModelID(appUserModelId);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TaskbarForm());
                GC.KeepAlive(mutex);
            }
        }
    }
}
