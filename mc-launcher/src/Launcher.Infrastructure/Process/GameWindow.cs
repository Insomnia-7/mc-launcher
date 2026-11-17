using System.Runtime.InteropServices;
using System.Text;

namespace Launcher.Infrastructure.Processes;

internal static class GameWindow
{
    public static bool TryShow(System.Diagnostics.Process process)
    {
        if(!OperatingSystem.IsWindows())return false;
        try
        {
            process.Refresh();if(process.HasExited)return false;
            var handle=process.MainWindowHandle;
            if(handle==0)
                EnumWindows((window,_)=>
                {
                    GetWindowThreadProcessId(window,out var pid);
                    if(pid!=process.Id||GetWindow(window,4)!=0)return true;
                    var name=new StringBuilder(128);GetClassName(window,name,name.Capacity);
                    if(name.ToString() is not ("GLFW30" or "SDL_app"))return true;
                    handle=window;return false;
                },0);
            if(handle==0)return false;
            if(IsIconic(handle))ShowWindowAsync(handle,9);
            else if(!IsWindowVisible(handle))ShowWindowAsync(handle,5);
            SetForegroundWindow(handle);
            return IsWindowVisible(handle);
        }
        catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){return false;}
    }
    private delegate bool WindowCallback(nint window,nint state);
    [DllImport("user32.dll")]private static extern bool EnumWindows(WindowCallback callback,nint state);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out int pid);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint window,StringBuilder name,int count);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]private static extern bool ShowWindowAsync(nint window,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
}
