using System;using System.IO;using System.Runtime.InteropServices;using Microsoft.Win32.SafeHandles;
namespace LilithSetup {
 internal static class FileIdentity {
  [StructLayout(LayoutKind.Sequential)]struct Information {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out Information information);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
  public static string Of(SafeFileHandle handle){Information info=new Information();SetupCore.Require(!handle.IsInvalid&&GetFileInformationByHandle(handle,out info),"LBSETUP_FILE_IDENTITY_UNAVAILABLE");return info.Volume.ToString("X8")+":"+info.IndexHigh.ToString("X8")+info.IndexLow.ToString("X8");}
  public static string Directory(string path){SetupCore.NoReparse(path);using(var handle=CreateFile(path,0x80,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero))return Of(handle);}
 }
}
