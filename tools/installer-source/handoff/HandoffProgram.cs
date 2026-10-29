using System;using System.Windows.Forms;
namespace LilithSetup {
 static class HandoffProgram {
  [STAThread]static int Main(string[] args){try{SetupCore.Require(args.Length==2&&args[0]=="--handoff","LBWIZ_HANDOFF_ARGUMENT_INVALID");InstallerHandoff.RunHelper(args[1]);return 0;}catch(Exception error){MessageBox.Show("新版安装器尚未接续。旧安装包仍可打开，插件和配置没有因这次切换而被替换。\r\n"+SetupCore.SafeError(error),"LilithBridge · 安装器切换",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;}}
 }
}
