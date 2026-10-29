using System;
using System.IO;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
[assembly: AssemblyTitle("LilithBridge Installer Candidate")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: ComVisible(false)]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
namespace LilithSetup
{
    static class Program
    {
        [STAThread] static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                string root = null;
                if (args.Length == 2 && args[0] == "--root") root = args[1];
                else SetupCore.Require(args.Length == 0, "LBINSTALL_ONLY_ROOT_ARGUMENT_SUPPORTED_NO_KEYS");
                using (var form = new InstallerForm(AppDomain.CurrentDomain.BaseDirectory, root)) Application.Run(form);
                return 0;
            }
            catch (Exception error) { MessageBox.Show(InstallerCore.Explain(error), "LilithBridge 安装与配置", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}
