using System;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppPaths.InitializePortableStorage();
            Application.Run(new MainForm());
        }
    }
}
