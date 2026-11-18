# Fecha o app normalmente (salvando tudo), mesmo escondido na bandeja
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class CloseTurzx {
 delegate bool P(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
 public static void Close(uint pid) { EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid){ var s=new StringBuilder(64); GetWindowText(h,s,64); if(s.ToString()=="TURZX Screen") PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);} return true;}, IntPtr.Zero); }
}
'@
Get-Process TurzxScreen -ErrorAction SilentlyContinue | ForEach-Object {
  [CloseTurzx]::Close([uint32]$_.Id)
  [void]$_.WaitForExit(10000)
}
