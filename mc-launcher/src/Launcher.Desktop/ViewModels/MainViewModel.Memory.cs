using System.Runtime.InteropServices;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Install;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    private long _physicalMemoryMb = Math.Max(1024,GC.GetGCMemoryInfo().TotalAvailableMemoryBytes/1048576);
    private long _availableMemoryMb;
    public bool ManualMemory => !AutomaticMemory;
    public double MemorySliderMaximum => Math.Max(MemoryMb,Math.Min(InstanceSettings.MaxMemoryMb,Math.Max(1024,_physicalMemoryMb-1024)));
    public string AllocatedMemoryText => $"{MemoryMb/1024d:0.#} GB";
    public string MemoryModeText => AutomaticMemory ? "启动器自动分配，启动游戏时重新计算。" : "拖动滑块选择，每格 0.5 GB。";
    public string SystemMemoryText => $"{(_physicalMemoryMb-_availableMemoryMb)/1024d:0.#} GB / {_physicalMemoryMb/1024d:0.#} GB";
    public string RemainingMemoryText => $"分配后预计可用 {Math.Max(0,_availableMemoryMb-MemoryMb)/1024d:0.#} GB";
    public string MemoryPressureText => MemoryMb>_availableMemoryMb ? "当前可用内存低于分配值，建议关闭其他程序或减少分配。" : "";
    public ColumnDefinitions MemorySegments
    {
        get
        {
            var used=Math.Max(0,_physicalMemoryMb-_availableMemoryMb);
            var allocated=Math.Min(MemoryMb,_availableMemoryMb);
            var remaining=Math.Max(0,_availableMemoryMb-allocated);
            return new(FormattableString.Invariant($"{Math.Max(0.01,used)}*,{Math.Max(0.01,allocated)}*,{Math.Max(0.01,remaining)}*"));
        }
    }
    [RelayCommand] private void SelectMemoryMode(string? mode) => AutomaticMemory=mode!="manual";
    private void InitializeMemoryControl()
    {
        if(AutomaticMemory)MemoryMb=InstanceSettings.AdaptiveMemoryMb();
        RefreshMemoryStatus();
        PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(DetailTab) or nameof(SelectedInstance))RefreshMemoryStatus();
            if(e.PropertyName is nameof(MemoryMb) or nameof(AutomaticMemory))
            {
                foreach(var name in new[]{nameof(ManualMemory),nameof(MemorySliderMaximum),nameof(AllocatedMemoryText),nameof(MemoryModeText),nameof(RemainingMemoryText),nameof(MemoryPressureText),nameof(MemorySegments)})OnPropertyChanged(name);
            }
        };
    }
    [RelayCommand] private void RefreshMemoryStatus()
    {
        if(OperatingSystem.IsWindows())
        {
            var state=new NativeMemory{Length=(uint)Marshal.SizeOf<NativeMemory>()};
            if(GlobalMemoryStatusEx(ref state)){_physicalMemoryMb=(long)(state.TotalPhysical/1048576);_availableMemoryMb=(long)(state.AvailablePhysical/1048576);}
        }
        else _availableMemoryMb=Math.Max(0,_physicalMemoryMb-GC.GetGCMemoryInfo().MemoryLoadBytes/1048576);
        foreach(var name in new[]{nameof(MemorySliderMaximum),nameof(SystemMemoryText),nameof(RemainingMemoryText),nameof(MemoryPressureText),nameof(MemorySegments)})OnPropertyChanged(name);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMemory
    {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemory status);
}
