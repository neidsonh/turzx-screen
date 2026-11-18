# Teste do editor: manda cliques/arrastos direto para a janela (nao mexe no mouse real) e captura o resultado
param([string]$OutDir)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class E {
 public delegate bool P(IntPtr h, IntPtr l);
 [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
 [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, P p, IntPtr l);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 [DllImport("user32.dll")] public static extern bool SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 public static IntPtr Top(string t) { IntPtr f = IntPtr.Zero; EnumWindows((h,l)=>{ var s=new StringBuilder(128); GetWindowText(h,s,128); if(s.ToString()==t){f=h;return false;} return true;}, IntPtr.Zero); return f; }
 public static IntPtr Canvas(IntPtr top) { IntPtr f=IntPtr.Zero; EnumChildWindows(top,(h,l)=>{ R r; GetClientRect(h,out r); if(r.Ri==720&&r.B==480){f=h;return false;} return true;}, IntPtr.Zero); return f; }
 public static IntPtr Button(IntPtr top, string t) { IntPtr f=IntPtr.Zero; EnumChildWindows(top,(h,l)=>{ var s=new StringBuilder(64); GetWindowText(h,s,64); if(s.ToString()==t){f=h;return false;} return true;}, IntPtr.Zero); return f; }
}
'@
function MouseParam($x, $y) { [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF)) }
function Drag($c, $x1, $y1, $x2, $y2) {
  [E]::SendMessage($c, 0x201, [IntPtr]1, (MouseParam -x $x1 -y $y1)) | Out-Null
  1..6 | % { $x = $x1 + ($x2 - $x1) * $_ / 6; $y = $y1 + ($y2 - $y1) * $_ / 6; [E]::SendMessage($c, 0x200, [IntPtr]1, (MouseParam -x ([int]$x) -y ([int]$y))) | Out-Null }
  [E]::SendMessage($c, 0x202, [IntPtr]0, (MouseParam -x $x2 -y $y2)) | Out-Null
}
function Click($top, $t) { $b = [E]::Button($top, $t); if ($b -eq [IntPtr]::Zero) { throw "botao $t nao encontrado" }; [E]::SendMessage($b, 0xF5, [IntPtr]0, [IntPtr]0) | Out-Null }
function Snap($top, $f) {
  [E]::SetForegroundWindow($top) | Out-Null; Start-Sleep -Milliseconds 700
  $r = New-Object E+R; [E]::GetWindowRect($top, [ref]$r) | Out-Null
  $b = New-Object Drawing.Bitmap ($r.Ri-$r.L), ($r.B-$r.T); $g = [Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, $b.Size); $b.Save($f); $g.Dispose(); $b.Dispose()
}
$top = [IntPtr]::Zero; 1..30 | % { if ($top -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300; $top = [E]::Top('Editor de layout') } }
if ($top -eq [IntPtr]::Zero) { throw 'editor nao abriu' }
$c = [E]::Canvas($top); if ($c -eq [IntPtr]::Zero) { throw 'canvas nao encontrado' }
Snap $top "$OutDir\ed1.png"
# adiciona medidor (nasce em 16,16 = 24,24 na tela com zoom 1,5), arrasta para o centro e aumenta pelo canto
Click $top 'Medidor'; Start-Sleep -Milliseconds 300
Drag $c 90 90 330 200
# canto inferior direito: (16+120)+... medidor 96 px => largura 144 na tela; origem apos mover = 24+240,24+110
Drag $c 408 278 470 340
Start-Sleep -Milliseconds 500
Snap $top "$OutDir\ed2.png"
Click $top 'Desfazer'; Start-Sleep -Milliseconds 300
Click $top 'Desfazer'; Start-Sleep -Milliseconds 300
Snap $top "$OutDir\ed3.png"
Click $top 'Desfazer'; Start-Sleep -Milliseconds 300
"ok"
