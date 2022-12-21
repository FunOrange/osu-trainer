using System;
using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace osu_trainer
{
    public static class Program
    {
        public static PrivateFontCollection FontCollection { get; } = new PrivateFontCollection();

        [STAThread]
        public static void Main()
        {
            try
            {
                //var updaterProc = Process.Start("updater.exe");
                //updaterProc.WaitForExit();
            }
            catch (Exception e)
            {
                // failed to update, who cares
                Console.WriteLine("Failed to update lol");
                Console.WriteLine(e);
            }

            AddFont(FontCollection, Properties.Resources.Comfortaa_Bold);

            Application.CurrentCulture = new CultureInfo("en-US", false);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void AddFont(PrivateFontCollection collection, byte[] bytes)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr pointer = handle.AddrOfPinnedObject();
            try
            {
                collection.AddMemoryFont(pointer, bytes.Length);
            }
            finally
            {
                handle.Free();
            }
        }
    }
}